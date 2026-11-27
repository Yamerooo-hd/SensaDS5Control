using System.Reflection;
using System.Text;

namespace SensaDS5Control.App;

public static class BundledFirmware
{
    public static string Version
    {
        get
        {
            var bytes = LoadBytes("fw_version.txt");
            if (bytes != null)
            {
                var txt = Encoding.UTF8.GetString(bytes).Trim();
                if (!string.IsNullOrEmpty(txt)) return txt;
            }
            return "0.2.0";
        }
    }

    public static byte[]? LoadBridgeUf2() => LoadBytes("SensaBridge.uf2");
    public static byte[]? LoadNukeUf2() => LoadBytes("flash_nuke.uf2");

    private static byte[]? LoadBytes(string fileName)
    {
        try
        {
            var localPath = Path.Combine(AppContext.BaseDirectory, "BundledFirmware", fileName);
            if (File.Exists(localPath)) return File.ReadAllBytes(localPath);

            var asm = Assembly.GetExecutingAssembly();
            var resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase) ||
                                     n.Equals(fileName, StringComparison.OrdinalIgnoreCase));
            if (resName == null) return null;
            using var s = asm.GetManifestResourceStream(resName);
            if (s == null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }
}
