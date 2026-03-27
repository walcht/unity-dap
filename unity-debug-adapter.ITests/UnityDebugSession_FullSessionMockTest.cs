using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityDebugAdapter;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System;
using System.Text;

namespace unity_debug_adapter.ITests
{
  /// <summary>
  /// End-to-End testing of the Unity debug session. Initially, I thought about making this trully end-to-end but
  /// launching Neovim, setting up breakpoints, steping in/out is simply too much work and too error-prone.
  ///
  /// What I do instead is to supply DAP requests (that were captured in a real debugging session from Neovim <-> Unity)
  /// and send them to this debug adapter and assert the responses (here I am assuming they remain the same - in case
  /// they don't the response has to be parsed and only fields we care about have to be asserted).
  ///
  ///   log.txt: contains a request per line (without the Content-Length: (\d+)\r\n\r\n sequence)
  /// </summary>
  [TestFixture]
  public class UnityDebugSession_FullSessionMockTest
  {
    // private static readonly Regex UNITY_VERSION_REGEX = new Regex(@"(\d+)\.(\d+)");
    private Process m_UnityProcess;
    private Process m_DebugAdapterProcess;
    private readonly Regex re = new Regex(@"Content-Length: (\d+)\r\n\r\n");

    [OneTimeSetUp]
    public void StartTest()
    {
      // setup tracer/logger
      Trace.Listeners.Add(new ConsoleTraceListener());


      // find Unity installation path
#if Windows
      string unity_hub_editor_dir = "C:/Program Files/Unity/Hub/Editor";
#elif Linux
      string unity_hub_editor_dir = "/home/<user>/Unity/Hub/Editor";
#else
      string unity_hub_editor_dir = "/Applications/Unity/Hub/Editor";
#endif

      // we test on 2022.3.X so we expect an editor of that version to be installed
      // TODO: wtf - how to make this shit work?
      var candidateEditors = Directory.EnumerateDirectories(unity_hub_editor_dir)
        .Where(unity_editor_version =>
            {
              Trace.TraceInformation("found Unity editor version: " + unity_editor_version);
              return unity_editor_version.StartsWith("2022.3.");
            });

#if Windows
      // string unity_exe = Path.Combine(unity_hub_editor_dir, candidateEditors.FirstOrDefault(), "Editor", "Unity.exe");
      string unity_exe = @"C:\Program Files\Unity\Hub\Editor\2022.3.62f3\Editor\Unity.exe";
#elif Linux
      string unity_exe = Path.Combine(unity_hub_editor_dir, candidateEditors.FirstOrDefault(), "Editor", "Unity");
#else // MacOS
      string unity_exe = Path.Combine(unity_hub_editor_dir, candidateEditors.FirstOrDefault(), "Unity.app", "Contents", "MacOS", "Unity");
#endif

      if (string.IsNullOrWhiteSpace(unity_exe))
      {
        Assert.Fail($"could not find Unity Editor 2022.3.X installed (looked in {unity_hub_editor_dir})");
      }

      // if the provided Unity project is invalid, Unity simply doesn't launch and weirdly exits with a 0 exit code
      string unity_test_project = @"C:\Users\walid\Desktop\unity_test_project";  // Path.GetFullPath("./unity_test_project")
      Trace.TraceInformation($"Unity executable is set to {unity_exe}");

      // start debuggee (i.e., Unity) on the unity_test_project
      m_UnityProcess = new Process();
      m_UnityProcess.StartInfo.FileName = unity_exe;  // -batchmode -nographics
      m_UnityProcess.StartInfo.Arguments = $"-projectPath {unity_test_project}";
      m_UnityProcess.StartInfo.WindowStyle = ProcessWindowStyle.Normal;
      m_UnityProcess.StartInfo.CreateNoWindow = false;
      m_UnityProcess.StartInfo.UseShellExecute = false;
      m_UnityProcess.StartInfo.RedirectStandardOutput = false;
      m_UnityProcess.StartInfo.RedirectStandardError = false;
      m_UnityProcess.StartInfo.RedirectStandardInput = false;
      m_UnityProcess.Start();

      Trace.TraceInformation($"started Unity Editor process: {m_UnityProcess.StartInfo.FileName} {m_UnityProcess.StartInfo.Arguments}");

      // start debug adapter in another child process
      m_DebugAdapterProcess = new Process();
      m_DebugAdapterProcess.StartInfo.FileName = "../bin/Release/unity-debug-adapter.exe";
      m_DebugAdapterProcess.StartInfo.Arguments = "--log-level=none";
      m_DebugAdapterProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
      m_DebugAdapterProcess.StartInfo.CreateNoWindow = true;
      m_DebugAdapterProcess.StartInfo.UseShellExecute = false;
      m_DebugAdapterProcess.StartInfo.RedirectStandardOutput = true;
      m_DebugAdapterProcess.StartInfo.RedirectStandardError = true;
      m_DebugAdapterProcess.StartInfo.RedirectStandardInput = true;
      m_DebugAdapterProcess.Start();

      Trace.TraceInformation($"started debug adapter process: {m_DebugAdapterProcess.StartInfo.FileName} {m_DebugAdapterProcess.StartInfo.Arguments}");

      // first, filter out responses/requests from the log file and save them in a string list
      var requests = new SortedDictionary<int, string>();
      var responses = new Dictionary<int, string>();
      int maxResponseLen = 0;

      foreach (string l in File.ReadAllLines("./log.txt"))
      {
        // because the logger logs \r\n\r\n sequence as rnrn
        var _l = l.Replace("rnrn", "\r\n\r\n");
        var m = re.Match(_l);
        if (!m.Success || m.Groups.Count < 2)
          continue;

        // l is always encoded in UTF8 so we can safely just use Length

        string body = _l.Substring(m.Index + "Content-Length: ".Length + m.Groups[1].Length + 4);
        var parsedJson = JObject.Parse(body);
        if (parsedJson == null)
        {
          // TODO: error out
          continue;
        }

        var _type = (string?)parsedJson["type"];
        if (string.IsNullOrWhiteSpace(_type))
        {
          // TODO: fail test if string is null
          continue;
        }

        // don't care about events for the moment
        if (_type == "event")
          continue;

        if (_type == "request")
        {
          var request_seq = (int?)parsedJson["seq"];
          if (request_seq == null)
          {
            // TODO: error out
            continue;
          }
          requests.Add(request_seq.Value, _l.Substring(m.Index));
          continue;
        }

        if (_type == "response")
        {
          var request_seq = (int?)parsedJson["request_seq"];
          if (request_seq == null)
          {
            // TODO: error out
            continue;
          }
          responses.Add(request_seq.Value, _l.Substring(m.Index));
          maxResponseLen = Math.Max(maxResponseLen, _l.Length - m.Index);
          continue;
        }

        // TODO: error
      }

      Trace.TraceInformation($"parsed {responses.Count} responses from log.txt");
      Trace.TraceInformation($"parsed {requests.Count} requests from log.txt");
      Trace.TraceInformation($"max response length: {maxResponseLen}");

      char[] buffer = new char[maxResponseLen];
      foreach (var request in requests)
      {
        int requestSeq = request.Key;
        string requestStr = request.Value;
        m_DebugAdapterProcess.StandardInput.Write(requestStr);
        // now wait for response
        var nbrCharsReceived = m_DebugAdapterProcess.StandardOutput.Read(buffer, 0, buffer.Length);
        var responseStr = new string(buffer, 0, nbrCharsReceived);
        if (string.IsNullOrWhiteSpace(responseStr))
        {
          // TODO: test fail here
          continue;
        }

        var parsedJson = JObject.Parse(responseStr);
        if (parsedJson == null)
        {
          // TODO: test fail here
          continue;
        }

        var _requestSeq = (int?)parsedJson["request_seq"];
        if (_requestSeq == null)
        {
          // TODO: test fail here
          continue;
        }

        // fetch the response from the stored responses from log.txt
        string? expectedResponse = responses[_requestSeq.Value];
        if (string.IsNullOrWhiteSpace(expectedResponse))
        {
          // TODO: test fail here
          continue;
        }

        // TODO: test if expectedResponse == response

      }

      /*
      foreach (string request in requests)
      {

        m_DebugAdapterProcess.StandardInput.Write(request);
        // now wait for a response
        string response = m_DebugAdapterProcess.StandardOutput.ReadLine();

        var m = re.Match(response);

        // TODO: get 

        m_DebugAdapterProcess.StandardInput.Write($"Content-Length: ({l.Length})\r\n\r\n{l}");
        //  then wait for the response on stdout
        string response = m_DebugAdapterProcess.StandardOutput.ReadLine();
        // Assert
        Assert.That(response);
      }
      */

      RunSession(Console.OpenStandardInput(), Console.OpenStandardOutput());

      m_UnityProcess.WaitForExit();
      if (m_UnityProcess.ExitCode != 0)
      {
        Assert.Fail($"Unity process exited with non-0 exit code: {m_UnityProcess.ExitCode}");
      }

      Assert.Pass();

      // get port:ip Unity Editor is listening on
      // send attach request
    }

    [Test]
    public void Test1()
    {
      Assert.Pass();
    }

    [OneTimeTearDown]
    public void EndTest()
    {
      Trace.TraceInformation("killing Unity process ...");

      try
      {
        m_UnityProcess.Kill();
        m_UnityProcess.WaitForExit();
        m_UnityProcess.Dispose();
      }
      catch (System.InvalidOperationException)
      {
        // probably means that process has already exited
      }

      Trace.TraceInformation("killing debug adapter process ...");

      try
      {
        m_DebugAdapterProcess.Kill();
        m_DebugAdapterProcess.WaitForExit();
        m_DebugAdapterProcess.Dispose();
      }
      catch (System.InvalidOperationException)
      {
        // probably means that process has already exited
      }

      Trace.TraceInformation("Unity process killed successfully");

      Trace.Flush();
      // close Unity Editor
      // close logger
    }
  }
}


