using System;
using System.IO;

namespace UnityDebugAdapter
{
  internal class Program
  {
    private static void Main(string[] argv)
    {
      // parse command line arguments
      foreach (var a in argv)
      {
        switch (a)
        {
          case "--log-level=trace":
            Logger.SetLogLevel(LogLevel.TRACE);
            break;
          case "--log-level=debug":
            Logger.SetLogLevel(LogLevel.DEBUG);
            break;
          case "--log-level=info":
            Logger.SetLogLevel(LogLevel.INFORMATION);
            break;
          case "--log-level=warn":
            Logger.SetLogLevel(LogLevel.WARNING);
            break;
          case "--log-level=error":
            Logger.SetLogLevel(LogLevel.ERROR);
            break;
          case "--log-level=critical":
            Logger.SetLogLevel(LogLevel.CRITICAL);
            break;
          case "--log-level=none":
            Logger.SetLogLevel(LogLevel.NONE);
            break;
          default:
            if (a.StartsWith("--log-file="))
            {
              // logger is set by default to write to stderr
              Logger.SetLogStream(File.CreateText(a.Substring("--log-file=".Length)));
            }
            break;
        }
      }

      // stdin/stdout
      Logger.LogInfo("waiting for debug protocol on stdin/stdout");
      RunSession(Console.OpenStandardInput(), Console.OpenStandardOutput());
    }

    private static void RunSession(Stream inputStream, Stream outputStream)
    {
      DebugSession debugSession = new UnityDebugSession();
      debugSession.Start(inputStream, outputStream).Wait();
      Logger.Disconnect();
    }
  }
}
