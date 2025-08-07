using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace UnityDebugAdapter
{
  internal class Program
  {
    const int DEFAULT_PORT = 4711;

    private static bool trace_requests;
    private static bool trace_responses;
    static string LOG_FILE_PATH = null;

    private static void Main(string[] argv)
    {
      // parse command line arguments
      foreach (var a in argv)
      {
        if (a == "--list")
        {
          // TODO
          return;
        }
        switch (a)
        {
          case "--trace":
            trace_requests = true;
            break;
          case "--trace=response":
            trace_requests = true;
            trace_responses = true;
            break;
          default:
            if (a.StartsWith("--log-file="))
            {
              LOG_FILE_PATH = a.Substring("--log-file=".Length);
            }
            break;
        }
      }

      if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("mono_debug_logfile")) == false)
      {
        LOG_FILE_PATH = Environment.GetEnvironmentVariable("mono_debug_logfile");
        trace_requests = true;
        trace_responses = true;
      }

      // stdin/stdout
      Log("waiting for debug protocol on stdin/stdout");
      RunSession(Console.OpenStandardInput(), Console.OpenStandardOutput());
    }

    static TextWriter logFile;

    public static void Log(bool predicate, string msg, params object[] _)
    {
      if (predicate)
      {
        Log(msg);
      }
    }

    public static void Log(string msg, params object[] _)
    {
      try
      {
        // don't write to StdOut because it is used to communicate with the client!
        Console.Error.WriteLine(msg);

        if (LOG_FILE_PATH != null)
        {
          if (logFile == null)
          {
            logFile = File.CreateText(LOG_FILE_PATH);
          }
          logFile.WriteLine(string.Format("{0} {1}", DateTime.UtcNow.ToLongTimeString(), msg));
        }
      }
      catch (Exception ex)
      {
        if (LOG_FILE_PATH != null)
        {
          try
          {
            File.WriteAllText(LOG_FILE_PATH + ".err", ex.ToString());
          }
          catch
          {
          }
        }

        throw;
      }
    }

    private static void RunSession(Stream inputStream, Stream outputStream)
    {
      DebugSession debugSession = new UnityDebugSession
      {
        TRACE = trace_requests,
        TRACE_RESPONSE = trace_responses
      };
      debugSession.Start(inputStream, outputStream).Wait();

      if (logFile != null)
      {
        logFile.Flush();
        logFile.Close();
        logFile = null;
      }
    }
  }
}
