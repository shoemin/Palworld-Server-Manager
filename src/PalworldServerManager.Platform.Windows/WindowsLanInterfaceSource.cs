using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Platform.Windows;

public sealed class WindowsLanInterfaceSource : ILanDiscoveryInterfaceSource
{
    internal const int MaximumAddresses = 256;
    internal sealed record AdapterAddress(Guid Id, int Index, IPAddress Address, int PrefixLength);
    internal sealed record InterfaceState(ulong Luid, uint Index, Guid Id, uint Type, uint TunnelType, uint MediaType,
        uint PhysicalMediumType, uint AccessType, uint DirectionType, byte Flags, uint OperStatus, uint AdminStatus, uint MediaState, uint ConnectionType);

    public IReadOnlyList<LanDiscoveryLink> Read()
    {
        var addresses = new List<AdapterAddress>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (addresses.Count >= MaximumAddresses) break;
            try
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || !Guid.TryParse(adapter.Id, out var id) ||
                    !adapter.Supports(NetworkInterfaceComponent.IPv4)) continue;
                var properties = adapter.GetIPProperties(); var index = properties.GetIPv4Properties().Index;
                foreach (var address in properties.UnicastAddresses)
                {
                    if (addresses.Count >= MaximumAddresses) break;
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork && address.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred)
                        addresses.Add(new(id, index, address.Address, address.PrefixLength));
                }
            }
            catch (NetworkInformationException) { /* An unreadable/disappearing adapter does not qualify. */ }
        }
        return Select(addresses, ReadNative);
    }

    internal static IReadOnlyList<LanDiscoveryLink> Select(IEnumerable<AdapterAddress> addresses, Func<uint, InterfaceState?> readNative)
    {
        var observations = new Dictionary<uint, InterfaceState?>(); var result = new HashSet<LanDiscoveryLink>();
        foreach (var address in addresses.Take(MaximumAddresses))
        {
            if (address.Index <= 0 || address.Id == Guid.Empty) continue;
            var index = (uint)address.Index;
            if (!observations.TryGetValue(index, out var state)) observations.Add(index, state = readNative(index));
            if (state is null || state.Index != index || state.Id != address.Id || state.Luid == 0 || !Eligible(state)) continue;
            try { result.Add(new(state.Luid, state.Id, address.Index, address.Address, address.PrefixLength)); }
            catch (ArgumentException) { /* Invalid or non-host IPv4 subnet observation. */ }
        }
        return result.OrderBy(link => link.InterfaceIndex).ThenBy(link => link.LocalAddress.ToString(), StringComparer.Ordinal).ThenBy(link => link.PrefixLength).ToArray();
    }
    private static bool Eligible(InterfaceState state) => state.Flags == 0x05 && state.OperStatus == 1 && state.AdminStatus == 1 &&
        state.MediaState == 1 && state.ConnectionType == 1 && state.TunnelType == 0 && state.AccessType == 2 && state.DirectionType == 0 &&
        ((state.Type == 6 && state.PhysicalMediumType == 14 && state.MediaType is 0 or 5) ||
         (state.Type == 71 && state.PhysicalMediumType is 1 or 9 && state.MediaType is 0 or 16));

    internal static InterfaceState? ReadNative(uint index)
    {
        var row = new NativeRow { InterfaceIndex = index, Alias = "", Description = "", PhysicalAddress = new byte[32], PermanentPhysicalAddress = new byte[32] };
        if (GetIfEntry2(ref row) != 0) return null;
        return new(row.InterfaceLuid, row.InterfaceIndex, row.InterfaceGuid, row.Type, row.TunnelType, row.MediaType, row.PhysicalMediumType,
            row.AccessType, row.DirectionType, row.Flags, row.OperStatus, row.AdminStatus, row.MediaConnectState, row.ConnectionType);
    }
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetIfEntry2(ref NativeRow row);

    // Full SDK MIB_IF_ROW2 layout. The separate C++ SDK probe validates size/offsets.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NativeRow
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string? Alias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string? Description;
        public uint PhysicalAddressLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[]? PhysicalAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[]? PermanentPhysicalAddress;
        public uint Mtu, Type, TunnelType, MediaType, PhysicalMediumType, AccessType, DirectionType;
        public byte Flags;
        public uint OperStatus, AdminStatus, MediaConnectState;
        public Guid NetworkGuid;
        public uint ConnectionType;
        public ulong TransmitLinkSpeed, ReceiveLinkSpeed, InOctets, InUcastPkts, InNUcastPkts, InDiscards, InErrors, InUnknownProtos,
            InUcastOctets, InMulticastOctets, InBroadcastOctets, OutOctets, OutUcastPkts, OutNUcastPkts, OutDiscards, OutErrors,
            OutUcastOctets, OutMulticastOctets, OutBroadcastOctets, OutQLen;
    }
}
