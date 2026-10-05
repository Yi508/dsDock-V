using System;
using System.IO;

namespace DsDock.Storage;

internal enum DataRootSource
{
    ProgramDirectory,
    RoamingAppData,
    TempFallback,
}

/// <summary>
/// Data lives in data\ next to the executable when that is writable,
/// otherwise in %APPDATA%\桌面备忘录\ (spec: 数据存储与持久化).
/// </summary>
internal static class DataRoot
{
    public static string Root { get; private set; } = "";
    public static string Source { get; private set; } = "";
    public static DataRootSource SourceKind { get; private set; }
    public static string ProbeDetail { get; private set; } = "";
    public static string LogsDir => Path.Combine(Root, "logs");

    public static void Initialize(string? force = null)
    {
        string programData = Path.Combine(AppContext.BaseDirectory, "data");
        string roaming = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "桌面备忘录");

        switch (force)
        {
            case "appdata":
                Root = roaming; Source = "appdata(强制)"; SourceKind = DataRootSource.RoamingAppData;
                ProbeDetail = "由 --force-data-root=appdata 强制，未做可写性探测";
                break;
            case "temp":
                Root = Path.Combine(Path.GetTempPath(), "DsDockData");
                Source = "temp(强制)"; SourceKind = DataRootSource.TempFallback;
                ProbeDetail = "由 --force-data-root=temp 强制，未做可写性探测";
                break;
            default:
                if (TryProbe(programData, out string whyProgram))
                {
                    Root = programData; Source = "程序目录"; SourceKind = DataRootSource.ProgramDirectory;
                    ProbeDetail = $"程序目录 data\\ 可写: {programData}";
                }
                else if (TryProbe(roaming, out string whyRoaming))
                {
                    Root = roaming; Source = "%APPDATA%\\桌面备忘录"; SourceKind = DataRootSource.RoamingAppData;
                    ProbeDetail = $"程序目录不可写({whyProgram})，已回退到 {roaming}";
                }
                else
                {
                    Root = Path.Combine(Path.GetTempPath(), "DsDockData");
                    Source = "临时目录(兜底)"; SourceKind = DataRootSource.TempFallback;
                    ProbeDetail = $"程序目录({whyProgram}) 与 APPDATA({whyRoaming}) 均不可写，回退临时目录";
                }
                break;
        }

        Directory.CreateDirectory(LogsDir);
    }

    private static bool TryProbe(string dir, out string failure)
    {
        failure = "";
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".write-probe");
            File.WriteAllText(probe, "dsdock");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }
}
