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
        var context = GCHandle.Alloc(names);
        try
        {
            var request = new BrowseRequest
            {
                Version = QueryRequestVersion1,
                QueryName = serviceType,
                Callback = BrowseCallbackDelegate,
                QueryContext = GCHandle.ToIntPtr(context),
            };
            var cancel = new ServiceCancel();
            var status = DnsServiceBrowse(ref request, ref cancel);
            if (status != RequestPending)
                throw new InvalidOperationException($"DnsServiceBrowse failed with status {status}.");

            try
            {
                await Task.Delay(scanTime, cancellationToken);
            }
            finally
            {
                DnsServiceBrowseCancel(ref cancel);
            }

            lock (names)
                return names.ToList();
        }
        finally
        {
            context.Free();
        }
    }

    /// <summary>
    /// The host address, port and TXT values of one instance, or null if it didn't answer.
    /// </summary>
    public static async Task<ServiceInstance?> ResolveAsync(string instanceName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<ServiceInstance?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = GCHandle.Alloc(completion);
        var namePointer = Marshal.StringToHGlobalUni(instanceName);
        try
        {
            var request = new ResolveRequest
            {
                Version = QueryRequestVersion1,
                QueryName = namePointer,
                Callback = ResolveCallbackDelegate,
                QueryContext = GCHandle.ToIntPtr(context),
            };
            var cancel = new ServiceCancel();
            var status = DnsServiceResolve(ref request, ref cancel);
            if (status != RequestPending)
                return null;

            var finished = await Task.WhenAny(completion.Task, Task.Delay(timeout, cancellationToken));
            if (finished != completion.Task)
            {
                DnsServiceResolveCancel(ref cancel);
                return null;
            }
            return await completion.Task;
        }
        finally
        {
            Marshal.FreeHGlobal(namePointer);
            context.Free();
        }
    }

    private static void OnBrowse(uint status, IntPtr queryContext, IntPtr records)
    {
        if (records == IntPtr.Zero)
            return;

        var names = (HashSet<string>)GCHandle.FromIntPtr(queryContext).Target!;
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
        DnsRecordListFree(records, FreeRecordList);
    }

    private static void OnResolve(uint status, IntPtr queryContext, IntPtr instancePointer)
    {
        var completion = (TaskCompletionSource<ServiceInstance?>)GCHandle.FromIntPtr(queryContext).Target!;
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

    // Held in static fields so the garbage collector can't free them while Windows still calls them
    private static readonly BrowseCallback BrowseCallbackDelegate = OnBrowse;
    private static readonly ResolveCallback ResolveCallbackDelegate = OnResolve;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceCancel
    {
        public IntPtr Reserved;
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
    private static extern int DnsServiceBrowse(ref BrowseRequest request, ref ServiceCancel cancel);

    [DllImport("dnsapi.dll")]
    private static extern int DnsServiceBrowseCancel(ref ServiceCancel cancel);

    [DllImport("dnsapi.dll")]
    private static extern int DnsServiceResolve(ref ResolveRequest request, ref ServiceCancel cancel);

    [DllImport("dnsapi.dll")]
    private static extern int DnsServiceResolveCancel(ref ServiceCancel cancel);

    [DllImport("dnsapi.dll")]
    private static extern void DnsServiceFreeInstance(IntPtr instance);

    [DllImport("dnsapi.dll")]
    private static extern void DnsRecordListFree(IntPtr records, int freeType);
}
