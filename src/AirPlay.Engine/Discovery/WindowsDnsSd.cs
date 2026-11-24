using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;

namespace AirPlay.Engine.Discovery;

/// <summary>
/// DNS-SD through Windows' own mDNS service (dnsapi.dll, Windows 10 1809 and later).
/// Using the system's service means no firewall prompt and no fight over UDP 5353.
/// </summary>
internal static class WindowsDnsSd
{
    public sealed record ServiceInstance(string InstanceName, IPAddress? Address, int Port, IReadOnlyDictionary<string, string> Txt);

    /// <summary>
    /// Instance names such as "Bedroom 2._airplay._tcp.local" seen within the scan time.
    /// </summary>
    public static async Task<IReadOnlyList<string>> BrowseAsync(string serviceType, TimeSpan scanTime, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var context = Track(names);
        var cancel = AllocateCancel();
        try
        {
            var request = new BrowseRequest
            {
                Version = QueryRequestVersion1,
                QueryName = serviceType,
                Callback = BrowseCallbackDelegate,
                QueryContext = context,
            };
            var status = DnsServiceBrowse(ref request, cancel);
            if (status != RequestPending)
                throw new InvalidOperationException($"DnsServiceBrowse failed with status {status}.");

            try
            {
                await Task.Delay(scanTime, cancellationToken);
            }
            finally
            {
                DnsServiceBrowseCancel(cancel);
            }

            lock (names)
                return names.ToList();
        }
        finally
        {
            Pending.TryRemove(context, out _);
            Marshal.FreeHGlobal(cancel);
        }
    }

    /// <summary>
    /// The host address, port and TXT values of one instance, or null if it didn't answer.
    /// </summary>
    public static async Task<ServiceInstance?> ResolveAsync(string instanceName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<ServiceInstance?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = Track(completion);
        var cancel = AllocateCancel();
        var namePointer = Marshal.StringToHGlobalUni(instanceName);
        try
        {
            var request = new ResolveRequest
            {
                Version = QueryRequestVersion1,
                QueryName = namePointer,
                Callback = ResolveCallbackDelegate,
                QueryContext = context,
            };
            var status = DnsServiceResolve(ref request, cancel);
            if (status != RequestPending)
                return null;

            var finished = await Task.WhenAny(completion.Task, Task.Delay(timeout, cancellationToken));
            // Even an answered resolve keeps listening, on a thread of its own, until it's cancelled
            DnsServiceResolveCancel(cancel);
            return finished == completion.Task ? await completion.Task : null;
        }
        finally
        {
            Pending.TryRemove(context, out _);
            Marshal.FreeHGlobal(cancel);
            Marshal.FreeHGlobal(namePointer);
        }
    }

    /// <summary>
    /// Announces a service, e.g. "Upper Level._airplay._tcp.local", until the result is disposed.
    /// </summary>
    /// <param name="hostName">This PC's mDNS name, e.g. "DESKTOP-1.local".</param>
    public static async Task<IDisposable> RegisterAsync(string instanceName, string hostName, IPAddress address, int port,
        IReadOnlyList<KeyValuePair<string, string>> txt, CancellationToken cancellationToken)
    {
        var keys = txt.Select(t => Marshal.StringToHGlobalUni(t.Key)).ToArray();
        var values = txt.Select(t => Marshal.StringToHGlobalUni(t.Value)).ToArray();
        var ip4 = Marshal.AllocHGlobal(4);
        IntPtr instance;
        try
        {
            Marshal.Copy(address.MapToIPv4().GetAddressBytes(), 0, ip4, 4);
            // The instance keeps its own copies of everything passed in
            instance = DnsServiceConstructInstance(instanceName, hostName, ip4, IntPtr.Zero, (ushort)port, 0, 0, (uint)txt.Count, keys, values);
        }
        finally
        {
            foreach (var pointer in keys.Concat(values))
                Marshal.FreeHGlobal(pointer);
            Marshal.FreeHGlobal(ip4);
        }
        if (instance == IntPtr.Zero)
            throw new InvalidOperationException($"Couldn't describe {instanceName} for DNS-SD.");

        var completion = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = Track(completion);
        var request = new RegisterRequest
        {
            Version = QueryRequestVersion1,
            ServiceInstance = instance,
            Callback = RegisterCallbackDelegate,
            QueryContext = context,
        };
        uint status;
        try
        {
            status = DnsServiceRegister(ref request, IntPtr.Zero);
            if (status != RequestPending)
            {
                DnsServiceFreeInstance(instance);
                throw new InvalidOperationException($"DnsServiceRegister failed with status {status}.");
            }

            using (cancellationToken.Register(() => completion.TrySetCanceled()))
                status = await completion.Task;
        }
        finally
        {
            Pending.TryRemove(context, out _);
        }
        if (status != 0)
        {
            DnsServiceFreeInstance(instance);
            throw new InvalidOperationException($"Registering {instanceName} failed with status {status}.");
        }
        return new Registration(instance);
    }

    private sealed class Registration(IntPtr instance) : IDisposable
    {
        private IntPtr _instance = instance;

        public void Dispose()
        {
            if (_instance == IntPtr.Zero)
                return;
            var completion = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
            var context = Track(completion);
            var request = new RegisterRequest
            {
                Version = QueryRequestVersion1,
                ServiceInstance = _instance,
                Callback = RegisterCallbackDelegate,
                QueryContext = context,
            };
            // Windows goes on reading the instance until it reports the deregistration, so it's freed
            // only after that; one that never reports is left allocated rather than freed under Windows
            var pending = DnsServiceDeRegister(ref request, IntPtr.Zero) == RequestPending;
            if (!pending || completion.Task.Wait(TimeSpan.FromSeconds(2)))
                DnsServiceFreeInstance(_instance);
            Pending.TryRemove(context, out _);
            _instance = IntPtr.Zero;
        }
    }

    private static void OnRegistered(uint status, IntPtr queryContext, IntPtr instance)
    {
        if (Pending.TryGetValue(queryContext, out var state))
            ((TaskCompletionSource<uint>)state).TrySetResult(status);
    }

    private static void OnBrowse(uint status, IntPtr queryContext, IntPtr records)
    {
        if (records == IntPtr.Zero)
            return;

        if (Pending.TryGetValue(queryContext, out var state))
        {
            var names = (HashSet<string>)state;
            for (var record = records; record != IntPtr.Zero; record = Marshal.ReadIntPtr(record))
            {
                // DNS_RECORDW: pNext, pName, wType, wDataLength, Flags, dwTtl, dwReserved, then the data union
                if ((ushort)Marshal.ReadInt16(record, 2 * IntPtr.Size) != PtrRecordType)
                    continue;
                var target = Marshal.PtrToStringUni(Marshal.ReadIntPtr(record, RecordDataOffset));
                if (target is not null)
                    lock (names)
                        names.Add(target);
            }
        }
        DnsRecordListFree(records, FreeRecordList);
    }

    private static void OnResolve(uint status, IntPtr queryContext, IntPtr instancePointer)
    {
        if (!Pending.TryGetValue(queryContext, out var state))
        {
            if (instancePointer != IntPtr.Zero)
                DnsServiceFreeInstance(instancePointer);
            return;
        }

        var completion = (TaskCompletionSource<ServiceInstance?>)state;
        if (instancePointer == IntPtr.Zero)
        {
            completion.TrySetResult(null);
            return;
        }

        var instance = Marshal.PtrToStructure<ServiceInstanceNative>(instancePointer);
        var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < instance.PropertyCount; i++)
        {
            var key = Marshal.PtrToStringUni(Marshal.ReadIntPtr(instance.Keys, i * IntPtr.Size));
            var value = Marshal.PtrToStringUni(Marshal.ReadIntPtr(instance.Values, i * IntPtr.Size));
            if (key is not null)
                txt[key] = value ?? "";
        }

        // IP4_ADDRESS is stored in network byte order, which IPAddress(long) reads correctly on little-endian Windows
        var address = instance.Ip4Address != IntPtr.Zero
            ? new IPAddress((uint)Marshal.ReadInt32(instance.Ip4Address))
            : null;

        completion.TrySetResult(new ServiceInstance(Marshal.PtrToStringUni(instance.InstanceName) ?? "", address, instance.Port, txt));
        DnsServiceFreeInstance(instancePointer);
    }

    // Windows calls back on its own threads, and can call after a timeout or cancel has ended the
    // operation. Callbacks find their operation here by number and ignore one that has ended.
    private static readonly ConcurrentDictionary<IntPtr, object> Pending = new();
    private static long _lastContext;

    private static IntPtr Track(object operation)
    {
        var context = (IntPtr)Interlocked.Increment(ref _lastContext);
        Pending[context] = operation;
        return context;
    }

    // Windows keeps the cancel handle's address and writes to it after the call returns, so it must
    // live in unmanaged memory: a managed one moves with the garbage collector and Windows' writes
    // then corrupt whatever took its place
    private static IntPtr AllocateCancel()
    {
        var cancel = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(cancel, IntPtr.Zero);
        return cancel;
    }

    // Held in static fields so the garbage collector can't free them while Windows still calls them
    private static readonly BrowseCallback BrowseCallbackDelegate = OnBrowse;
    private static readonly ResolveCallback ResolveCallbackDelegate = OnResolve;
    private static readonly RegisterCallback RegisterCallbackDelegate = OnRegistered;

    private const uint QueryRequestVersion1 = 1;
    private const int RequestPending = 9506;
    private const ushort PtrRecordType = 12;
    private const int FreeRecordList = 1;
    // pNext and pName, then four 32-bit-or-smaller fields (wType+wDataLength, Flags, dwTtl, dwReserved)
    private static readonly int RecordDataOffset = 2 * IntPtr.Size + 16;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void BrowseCallback(uint status, IntPtr queryContext, IntPtr records);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ResolveCallback(uint status, IntPtr queryContext, IntPtr instance);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BrowseRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        [MarshalAs(UnmanagedType.LPWStr)] public string QueryName;
        public BrowseCallback Callback;
        public IntPtr QueryContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ResolveRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr QueryName;
        public ResolveCallback Callback;
        public IntPtr QueryContext;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void RegisterCallback(uint status, IntPtr queryContext, IntPtr instance);

    [StructLayout(LayoutKind.Sequential)]
    private struct RegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr ServiceInstance;
        public RegisterCallback Callback;
        public IntPtr QueryContext;
        public IntPtr Credentials;
        public int UnicastEnabled;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceInstanceNative
    {
        public IntPtr InstanceName;
        public IntPtr HostName;
        public IntPtr Ip4Address;
        public IntPtr Ip6Address;
        public ushort Port;
        public ushort Priority;
        public ushort Weight;
        public uint PropertyCount;
        public IntPtr Keys;
        public IntPtr Values;
        public uint InterfaceIndex;
    }

    [DllImport("dnsapi.dll")]
    private static extern int DnsServiceBrowse(ref BrowseRequest request, IntPtr cancel);

    [DllImport("dnsapi.dll")]
    private static extern int DnsServiceBrowseCancel(IntPtr cancel);

    [DllImport("dnsapi.dll")]
    private static extern int DnsServiceResolve(ref ResolveRequest request, IntPtr cancel);

    [DllImport("dnsapi.dll")]
    private static extern int DnsServiceResolveCancel(IntPtr cancel);

    [DllImport("dnsapi.dll")]
    private static extern void DnsServiceFreeInstance(IntPtr instance);

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DnsServiceConstructInstance(string serviceName, string hostName, IntPtr ip4, IntPtr ip6,
        ushort port, ushort priority, ushort weight, uint propertiesCount, IntPtr[] keys, IntPtr[] values);

    [DllImport("dnsapi.dll")]
    private static extern uint DnsServiceRegister(ref RegisterRequest request, IntPtr cancel);

    [DllImport("dnsapi.dll")]
    private static extern uint DnsServiceDeRegister(ref RegisterRequest request, IntPtr cancel);

    [DllImport("dnsapi.dll")]
    private static extern void DnsRecordListFree(IntPtr records, int freeType);
}
