using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System;
using Newtonsoft.Json;
using System.Threading;

namespace unity_debug_adapter.ITests
{
  /// <summary>
  /// End-to-End testing of the Unity debug session. Initially, I thought about making this trully end-to-end but
  /// launching Neovim, setting up breakpoints, steping in/out is simply too much work and too error-prone.
  ///
  /// What I do instead is to supply DAP m_Requests (that were captured in a real debugging session from Neovim <-> Unity)
  /// and send them to this debug adapter and assert the m_Responses (here I am assuming they remain the same - in case
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
    // private readonly Regex UNITY_EDITOR_PORT = new Regex(@"monoOptions.*127\.0\.0\.1:(\d+)");

    private SortedDictionary<int, string> m_Requests;
    private Dictionary<int, string> m_Responses;

    private int m_MaxResponseLen = 0;

    [OneTimeSetUp]
    public void StartTest()
    {
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
              TestContext.Progress.WriteLine("found Unity editor version: " + unity_editor_version);
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
      TestContext.Progress.WriteLine($"Unity executable is set to {unity_exe}");

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

      TestContext.Progress.WriteLine($"started Unity Editor process: {m_UnityProcess.StartInfo.FileName} {m_UnityProcess.StartInfo.Arguments}");
      // 56000 + <UNITY-EDITOR-PID> % 1000
      int port = 56000 + m_UnityProcess.Id % 1000;
      TestContext.Progress.WriteLine($"Unity Editor debugger is listening at 127.0.0.1:{port}");

      // wait for Unity Editor to launch
      Thread.Sleep(20_000);

      // start debug adapter in another child process
      m_DebugAdapterProcess = new Process();
      // TODO: replace path
      m_DebugAdapterProcess.StartInfo.FileName = "./unity-debug-adapter.exe";
      m_DebugAdapterProcess.StartInfo.Arguments = "--log-level=none";
      m_DebugAdapterProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
      m_DebugAdapterProcess.StartInfo.CreateNoWindow = true;
      m_DebugAdapterProcess.StartInfo.UseShellExecute = false;
      m_DebugAdapterProcess.StartInfo.RedirectStandardOutput = true;
      m_DebugAdapterProcess.StartInfo.RedirectStandardError = true;
      m_DebugAdapterProcess.StartInfo.RedirectStandardInput = true;
      m_DebugAdapterProcess.Start();

      TestContext.Progress.WriteLine($"started debug adapter process: {m_DebugAdapterProcess.StartInfo.FileName} {m_DebugAdapterProcess.StartInfo.Arguments}");

      // first, filter out m_Responses/m_Requests from the log file and save them in a string list
      m_Requests = new SortedDictionary<int, string>();
      m_Responses = new Dictionary<int, string>();
      m_MaxResponseLen = 0;

      foreach (string l in File.ReadAllLines("./mock-log.txt"))
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
          Assert.Fail($"parsed json log's string: {body} is null");
          return;
        }

        var _type = (string?)parsedJson["type"];
        if (string.IsNullOrWhiteSpace(_type))
        {
          Assert.Fail($"type attribute of parsed JSON from log string: {body} is null or whitespace");
          return;
        }

        // don't care about events for the moment
        if (_type == "event")
          continue;

        // if this is a threads command then ignore it (because it is non-deterministic...)
        var command = (string?)parsedJson["command"];
        if (command == "threads")
          continue;

        if (_type == "request")
        {
          var request_seq = (int?)parsedJson["seq"];
          if (request_seq == null)
          {
            Assert.Fail("request_seq attribute is null");
            return;
          }

          // if this is an attach request, then make sure to update the port
          var cmd = (string?)parsedJson["command"];
          if (cmd == "attach")
          {
            var args = parsedJson["arguments"];
            if (args == null)
            {
              Assert.Fail("arguments attribute is null");
              return;
            }
            args["port"] = port;
          }

          var v = parsedJson.ToString(Formatting.None);
          m_Requests.Add(request_seq.Value, $"Content-Length: {v.Length}\r\n\r\n{v}");
          continue;
        }

        if (_type == "response")
        {
          var request_seq = (int?)parsedJson["request_seq"];
          if (request_seq == null)
          {
            Assert.Fail("request_seq attribute is null");
            return;
          }

          m_Responses.Add(request_seq.Value, _l.Substring(m.Index));
          m_MaxResponseLen = Math.Max(m_MaxResponseLen, _l.Length - m.Index);
          continue;
        }

        Assert.Fail($"unkown type attribute in log line: {l}");
      }

      TestContext.Progress.WriteLine($"parsed {m_Responses.Count} m_Responses from log.txt");
      TestContext.Progress.WriteLine($"parsed {m_Requests.Count} m_Requests from log.txt");
      TestContext.Progress.WriteLine($"max response length: {m_MaxResponseLen}");
    }

    [Test]
    public void Test_LogRequestsAndResponses()
    {
      var m_PendingRequests = new List<int>();
      var buffer = new char[m_MaxResponseLen];
      var iter = m_Requests.GetEnumerator();
      iter.MoveNext();
      while (true)
      {
        // don't re-send the request if it is already pending
        if (!m_PendingRequests.Contains(iter.Current.Key))
        {
          string requestStr = iter.Current.Value;
          m_DebugAdapterProcess.StandardInput.Write(requestStr);

          // now wait for response
          TestContext.Progress.WriteLine($"sent request to unity-dap: {requestStr}");
          TestContext.Progress.WriteLine("waiting for response from unity-dap ...");

          m_PendingRequests.Add(iter.Current.Key);
        }

        var nbrCharsReceived = m_DebugAdapterProcess.StandardOutput.Read(buffer, 0, buffer.Length);
        var responseStr = new string(buffer, 0, nbrCharsReceived);
        if (string.IsNullOrWhiteSpace(responseStr))
        {
          Assert.Fail("received response string from unity-dap is null or whitespace");
          return;
        }

        var m = re.Match(responseStr);
        if (!m.Success || m.Groups.Count < 2)
        {
          Assert.Fail($@"failed to match 'Content-Length: (\d+)\r\n\r\n' from unity-dap response: {responseStr}");
          return;
        }

        var bodyStr = responseStr.Substring("Content-Length: ".Length + m.Groups[1].Length + 4);
        JObject? parsedJson;
        try
        {
          parsedJson = JObject.Parse(bodyStr);
        }
        catch (JsonReaderException)
        {
          Assert.Fail($"failed to parse JSON from: {bodyStr}");
          return;
        }
        if (parsedJson == null)
        {
          Assert.Fail($"parsed JSON from received response string: {bodyStr} from unity-dap is null");
          return;
        }

        // we don't care about other types (e.g., events)
        var _type = (string?)parsedJson["type"];
        if (_type != "response")
          continue;

        var _requestSeq = (int?)parsedJson["request_seq"];
        if (_requestSeq == null)
        {
          Assert.Fail($"request_seq attribute is null (from parsed json: {parsedJson})");
          return;
        }

        TestContext.Progress.WriteLine($"got response to request_seq: {_requestSeq}");

        // fetch the response from the stored m_Responses from log.txt
        string? expectedResponse = m_Responses[_requestSeq.Value];
        if (string.IsNullOrWhiteSpace(expectedResponse))
        {
          Assert.Fail($"could not find expected response in log responses (request_seq = {_requestSeq.Value})");
          return;
        }

        // this is probably not the best way to test that the response is correct because of sequence number
        // and its reliance on threads (which may be non-deterministic)
        Assert.That(responseStr, Is.EqualTo(expectedResponse));

        // move to next request (if any)
        if (!iter.MoveNext())
          break;
      }

      m_UnityProcess.WaitForExit();
    }

    [OneTimeTearDown]
    public void EndTest()
    {
      // close Unity Editor
      TestContext.Progress.WriteLine("killing Unity process ...");
      try
      {
        m_UnityProcess.Kill();
        m_UnityProcess.WaitForExit();
        m_UnityProcess.Dispose();
      }
      catch (InvalidOperationException) { /* probably means that process has already exited */ }

      // close unity-dap
      TestContext.Progress.WriteLine("killing debug adapter process ...");
      try
      {
        m_DebugAdapterProcess.Kill();
        m_DebugAdapterProcess.WaitForExit();
        m_DebugAdapterProcess.Dispose();
      }
      catch (InvalidOperationException) { /* probably means that process has already exited */ }

      TestContext.Progress.WriteLine("Unity process killed successfully");
    }
  }
}


