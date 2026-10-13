namespace Nitrogenesis.Sim.Io;

/// <summary>
/// Where the app keeps its data (PLAN §5.1): <c>%APPDATA%\Nitrogenesis\</c> (outside the program folder, per
/// APP-STANDARDS), with <c>sessions\</c>, <c>tracks\</c> and <c>settings.json</c>. The environment variable
/// <see cref="DataDirVariable"/> overrides the root (tests, measuring runs).
/// </summary>
/// <remarks>
/// <see cref="Environment.SpecialFolder.ApplicationData"/> is %APPDATA% (the roaming folder) on Windows and
/// ~/.config on Linux; it does not depend on Godot's user:// folder (which would be %APPDATA%\Godot\app_userdata\…).
/// </remarks>
public static class AppPaths
{
    public const string DataDirVariable = "NITROGENESIS_DATA";

    public static string DataRoot =>
        Environment.GetEnvironmentVariable(DataDirVariable) is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Nitrogenesis");

    public static string Sessions => Path.Combine(DataRoot, "sessions");
    public static string Tracks => Path.Combine(DataRoot, "tracks");
    public static string Settings => Path.Combine(DataRoot, "settings.json");
}
