using System;
using System.IO;

namespace UnityDebugAdapter
{
  enum LogLevel
  {
    TRACE = 0,
    DEBUG = 1,
    INFORMATION = 2,
    WARNING = 3,
    ERROR = 4,
    CRITICAL = 5,
    NONE = 6,
  }

  static class Logger
  {
    private static TextWriter s_LogFile = Console.Error;
    private static LogLevel s_LogLevel = LogLevel.INFORMATION;

    public static void SetLogStream(TextWriter stream)
    {
      s_LogFile.Flush();
      s_LogFile.Close();
      s_LogFile = stream;
    }

    public static void SetLogLevel(LogLevel logLevel) => s_LogLevel = logLevel;

    public static void LogTrace(string msg)
    {
      if (s_LogLevel > LogLevel.TRACE)
      {
        return;
      }

      s_LogFile.WriteLine($"{DateTime.Now:dd\\/MM\\/yyyy HH:mm:ss} [T] {msg}");
      s_LogFile.Flush();
    }

    public static void LogDebug(string msg)
    {
      if (s_LogLevel > LogLevel.DEBUG)
      {
        return;
      }

      s_LogFile.WriteLine($"{DateTime.Now:dd\\/MM\\/yyyy HH:mm:ss} [D] {msg}");
      s_LogFile.Flush();
    }

    public static void LogInfo(string msg)
    {
      if (s_LogLevel > LogLevel.INFORMATION)
      {
        return;
      }

      s_LogFile.WriteLine($"{DateTime.Now:dd\\/MM\\/yyyy HH:mm:ss} [I] {msg}");
      s_LogFile.Flush();
    }

    public static void LogWarn(string msg)
    {
      if (s_LogLevel > LogLevel.WARNING)
      {
        return;
      }

      s_LogFile.WriteLine($"{DateTime.Now:dd\\/MM\\/yyyy HH:mm:ss} [W] {msg}");
      s_LogFile.Flush();
    }

    public static void LogError(string msg)
    {
      if (s_LogLevel > LogLevel.ERROR)
      {
        return;
      }

      s_LogFile.WriteLine($"{DateTime.Now:dd\\/MM\\/yyyy HH:mm:ss} [E] {msg}");
      s_LogFile.Flush();
    }

    public static void LogCritical(string msg)
    {
      if (s_LogLevel > LogLevel.CRITICAL)
      {
        return;
      }

      s_LogFile.WriteLine($"{DateTime.Now:dd\\/MM\\/yyyy HH:mm:ss} [C] {msg}");
      s_LogFile.Flush();
    }

    public static void Disconnect()
    {
      s_LogFile.Flush();
      s_LogFile.Close();
    }
  }
}
