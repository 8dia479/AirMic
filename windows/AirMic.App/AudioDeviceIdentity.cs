using System.Runtime.InteropServices;
using System.Text;
using AirMic.Setup;

namespace AirMic.App;

internal static class AudioDeviceIdentity
{
    // Device-friendly names are user-editable. Do not report a renamed physical
    // speaker/mic as an AirMic cable. Resolve the endpoint's Windows PnP ancestry
    // and fail closed unless an exact Root\AirMic hardware ID is present.
    // Called only during device discovery/initialization, never on the render loop.
    public static bool IsAirMic(IPropertyStore properties)
    {
        var instanceKey = new PropertyKey { FormatId = new("78c34fc8-104a-4aca-9ea4-524d52996e57"), PropertyId = 256 };
        if (properties.GetValue(ref instanceKey, out PropVariant instance) < 0) return false;
        string? id;
        try { id = instance.Type == 31 ? Marshal.PtrToStringUni(instance.Pointer) : null; }
        finally { WindowsAudio.Clear(ref instance); }
        if (string.IsNullOrEmpty(id) || CM_Locate_DevNode(out uint device, id, 0) != 0) return false;
        var hardwareKey = new PropertyKey { FormatId = new("a45c254e-df1c-4efd-8020-67d146a850e0"), PropertyId = 3 };
        byte[] data = new byte[8192];
        for (int depth = 0; depth < 16; depth++)
        {
            uint bytes = (uint)data.Length;
            if (CM_Get_DevNode_Property(device, ref hardwareKey, out uint type, data, ref bytes, 0) == 0
                && type == 0x2012 && bytes <= data.Length && (bytes & 1) == 0
                && Encoding.Unicode.GetString(data, 0, (int)bytes).Split('\0')
                    .Any(value => value.Equals(DriverPackage.HardwareId, StringComparison.OrdinalIgnoreCase))) return true;
            if (CM_Get_Parent(out uint parent, device, 0) != 0 || parent == device) break;
            device = parent;
        }
        return false;
    }
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Locate_DevNodeW")]
    private static extern uint CM_Locate_DevNode(out uint device, string id, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static extern uint CM_Get_DevNode_Property(uint device, ref PropertyKey key, out uint type,
        [Out] byte[] data, ref uint bytes, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint device, uint flags);
}
