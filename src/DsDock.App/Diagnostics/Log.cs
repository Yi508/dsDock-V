using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DsDock.Diagnostics;

internal static class Log
{
    private static readonly object Gate = new();
    private static string? _path;
    public static readonly List<string> Lines = new();

    public static void Initialize(string logsDir, string fileName)
    {
        try
        {
            Directory.CreateDirectory(logsDir);
            _path = Path.Combine(logsDir, fileName);
            File.WriteAllText(_path, $"# DsDock log {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
            _path = null;
        }
    }

    public static string? Path0 => _path;

    public static void Info(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        lock (Gate)
        {
            Lines.Add(line);
            if (Lines.Count > 4000) Lines.RemoveRange(0, 1000);
        }
        try
        {
            if (_path != null) File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // logging must never take the app down
        }
    }
}
