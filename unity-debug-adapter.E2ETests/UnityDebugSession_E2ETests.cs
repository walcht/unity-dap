using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System;
using Newtonsoft.Json;
using System.Text;

namespace unity_debug_adapter.E2ETests
{
  /// <summary>
  /// End-to-End testing of the Unity debug session. Initially, I thought about making this trully end-to-end but
  /// launching Neovim, setting up breakpoints, steping in/out is simply too much work and too error-prone.
  ///
  /// What I do instead is to supply DAP m_Requests (that were captured in a real debugging session from Neovim <-> Unity)
  /// and send them to this debug adapter and assert the m_Responses (here I am assuming they remain the same - in case
  /// they don't the response has to be parsed and only fields we care about have to be asserted).
  ///
  ///   requests.txt: contains a request per line (with \r\n\r\n sequence replaced by rnrn)
  ///   responses.txt: contains a response per line (with \r\n\r\n sequence replaced by rnrn) 
  ///
  /// Currently implemented commands and tested commands are:
  ///
  ///   COMMAND                   IS TESTED?
  ///
  ///   initialize                YES
  ///   attach                    YES
  ///   setBreakpoints            YES
  ///   setExceptionBreakpoints   YES
  ///
  /// </summary>
  [TestFixture]
  public class UnityDebugSession_E2ETests
  {
    private Process m_UnityProcess;
    private Process m_DebugAdapterProcess;
    private readonly Regex re = new Regex(@"Content-Length: (\d+)\r\n\r\n");

    private readonly SortedDictionary<int, JObject> m_Requests = new SortedDictionary<int, JObject>();
    private readonly Dictionary<int, JObject> m_Responses = new Dictionary<int, JObject>();

    private readonly char[] m_ReceptionBuff = new char[4092];
    private readonly StringBuilder m_Sb = new StringBuilder(4092);
    private readonly Queue<string> m_Messages = new Queue<string>(16);
    private readonly IEqualityComparer<JObject> m_Comparer = new DapResponseComparer();

    private int m_BodyLen = -1;

    [OneTimeSetUp]
    public void StartTest()
    {
      // find Unity installation path
#if Windows
      string unity_hub_editor_dir = @"C:\Program Files\Unity\Hub\Editor";
#elif Linux
      string unity_hub_editor_dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Unity", "Hub", "Editor");
#else
      string unity_hub_editor_dir = "/Applications/Unity/Hub/Editor";
#endif

      // we test on 2022.3.X so we expect an editor of that version to be installed
      var unity_dir = Directory.EnumerateDirectories(unity_hub_editor_dir)
        .FirstOrDefault(unity_editor_version =>
            {
              TestContext.Progress.WriteLine("found Unity editor version: " + Path.GetFileName(unity_editor_version));
              return Path.GetFileName(unity_editor_version).StartsWith("2022.3.");
            });

      TestContext.Progress.WriteLine($"picked Unity editor version: {unity_dir}");
#if Windows
      string unity_exe = Path.Combine(unity_dir, "Editor", "Unity.exe");
#elif Linux
      string unity_exe = Path.Combine(unity_dir, "Editor", "Unity");
#else // MacOS
      string unity_exe = Path.Combine(unity_dir, "Unity.app", "Contents", "MacOS", "Unity");
#endif

      if (string.IsNullOrWhiteSpace(unity_exe))
      {
        Assert.Fail($"could not find Unity Editor 2022.3.X installed (looked in {unity_hub_editor_dir})");
      }

      // if the provided Unity project is invalid, Unity simply doesn't launch and weirdly exits with a 0 exit code
      // this is ugly (bin/Debug/) but trust me, copying Unity porjects around is a mess ...
      string unity_test_project = Path.GetFullPath("../../unity_test_project_2022_3");

      Assert.That(Directory.Exists(unity_test_project), Is.True);

      TestContext.Progress.WriteLine($"Unity executable is set to {unity_exe}");
      TestContext.Progress.WriteLine($"Unity 2022.3 test project is set to {unity_test_project}");

      // start debuggee (i.e., Unity) on the unity_test_project (don't put the '-nographics' or '-batchmode' flags!)
      m_UnityProcess = new Process();
      m_UnityProcess.StartInfo.FileName = unity_exe;
      m_UnityProcess.StartInfo.Arguments = $"-projectPath {unity_test_project} -executeMethod  UnityEditor.EditorApplication.EnterPlaymode";
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

      // start debug adapter in another child process
      m_DebugAdapterProcess = new Process();
      m_DebugAdapterProcess.StartInfo.FileName = "unity-debug-adapter.exe";
      m_DebugAdapterProcess.StartInfo.Arguments = "--log-level=none";
      m_DebugAdapterProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
      m_DebugAdapterProcess.StartInfo.CreateNoWindow = true;
      m_DebugAdapterProcess.StartInfo.UseShellExecute = false;
      m_DebugAdapterProcess.StartInfo.RedirectStandardOutput = true;
      m_DebugAdapterProcess.StartInfo.RedirectStandardError = true;
      m_DebugAdapterProcess.StartInfo.RedirectStandardInput = true;
      m_DebugAdapterProcess.Start();

      TestContext.Progress.WriteLine($"started debug adapter process: {m_DebugAdapterProcess.StartInfo.FileName} {m_DebugAdapterProcess.StartInfo.Arguments}");
      var testScriptFullPath = Path.Combine(unity_test_project, "Assets", "Scripts", "TestScript.cs");

      // parse requests and responses
      foreach (string l in File.ReadAllLines("log.txt"))
      {
        // because the logger logs \r\n\r\n sequence as rnrn
        var _l = l.Replace("rnrn", "\r\n\r\n");
        var m = re.Match(_l);
        if (!m.Success || m.Groups.Count < 2)
          continue;

        // l is always encoded in UTF8 so we can safely just use Length
        string body = _l.Substring(m.Index + "Content-Length: ".Length + m.Groups[1].Length + 4);
        var actual = JObject.Parse(body);
        if (actual == null)
        {
          Assert.Fail($"parsed json log's string: {body} is null");
          return;
        }

        var _type = (string?)actual["type"];
        if (string.IsNullOrWhiteSpace(_type))
        {
          Assert.Fail($"type attribute of parsed JSON from log string: {body} is null or whitespace");
          return;
        }

        if (_type == "request")
        {
          var request_seq = (int?)actual["seq"];
          if (request_seq == null)
          {
            Assert.Fail("seq attribute is null");
            return;
          }
          // if this is an attach request, then make sure to update the port
          var cmd = (string?)actual["command"];
          if (cmd == "attach")
          {
            var args = actual["arguments"];
            if (args == null)
            {
              Assert.Fail("arguments attribute is null");
              return;
            }
            args["port"] = port;
            args["name"] = $"Connect to Unity Editor instance at 127.0.0.1:{port}";
          }
          // if this is a 'setBreakpoints' request, then update the path
          else if (cmd == "setBreakpoints")
          {
            var args = actual["arguments"];
            if (args == null)
            {
              Assert.Fail("arguments attribute is null");
              return;
            }
            var src = args["source"];
            if (src == null)
            {
              Assert.Fail("source attribute is null");
              return;
            }
            src["path"] = testScriptFullPath;
          }
          m_Requests.Add(request_seq.Value, actual);
        }
        else if (_type == "response")
        {
          var request_seq = (int?)actual["request_seq"];
          if (request_seq == null)
          {
            Assert.Fail("request_seq attribute is null");
            return;
          }

          // if this is a stackTrace response, then make sure to update the path
          var cmd = (string?)actual["command"];
          if (cmd == "stackTrace")
          {
            var bdy = actual["body"];
            if (bdy == null)
            {
              Assert.Fail("body attribute is null");
              return;
            }

            var sfs = bdy["stackFrames"];
            if (sfs == null)
            {
              Assert.Fail("stackFrames attribute is null");
              return;
            }

            foreach (var sf in sfs)
            {
              var src = sf["source"];
              if (src == null)
              {
                Assert.Fail("source attribute is null");
                return;
              }

              src["path"] = testScriptFullPath;
            }
          }
          m_Responses.Add(request_seq.Value, actual);
        }
      }

      TestContext.Progress.WriteLine($"parsed {m_Requests.Count} requests from log.txt");
      TestContext.Progress.WriteLine($"parsed {m_Responses.Count} responses from log.txt");
      Assert.That(m_Requests, Has.Count.EqualTo(m_Responses.Count));
    }


    /// Attempts to parse one or more messages from string buffer. If message is fragmented, a subsequent call
    /// to this after having filled up the string buffer will re-construct that message.
    private void GetNextMessage()
    {
      while (true)
      {
        if (m_BodyLen >= 0)
        {
          if (m_ReceptionBuff.Length >= m_BodyLen)
          {
            m_Messages.Enqueue(m_Sb.ToString(0, m_BodyLen));
            m_Sb.Remove(0, m_BodyLen);
            m_BodyLen = -1;
            continue;  // handle next message (if any)
          }
          // currently held data is insufficient (keep reading)
          else
          {
            return;
          }
        }
        else
        {
          if (m_Sb.Length == 0)
            break;

          var s = m_Sb.ToString();

          Match m = re.Match(s);
          if (m.Success && m.Groups.Count == 2)
          {
            m_BodyLen = Convert.ToInt32(m.Groups[1].Value);
            m_Sb.Remove(0, m.Index + "Content-Length: ".Length + m.Groups[1].Length + 4);
            continue;
          }
          else
          {
            Assert.Fail($@"failed to match 'Content-Length: (\d+)\r\n\r\n' from unity-dap response: {s}");
            return;
          }
        }
      }

    }


    [Test]
    public void Test_LogRequestsAndResponses()
    {
      foreach (var item in m_Requests)
      {
        var request = item.Value;
        var requestStr = request.ToString(Formatting.None);
        m_DebugAdapterProcess.StandardInput.Write($"Content-Length: {requestStr.Length}\r\n\r\n{requestStr}");
        TestContext.Progress.WriteLine($"sent request to unity-dap: command={(string?)request["command"]}");

        // keep reading from unity-dap's stdout until we get the reponse to the request we just sent
        bool gotResponse = false;
        while (!gotResponse)
        {
          // message can be fragmented (even if buffer is large enough)!
          var nbrCharsReceived = m_DebugAdapterProcess.StandardOutput.Read(m_ReceptionBuff, 0, m_ReceptionBuff.Length);
          m_Sb.Append(m_ReceptionBuff, 0, nbrCharsReceived);
          GetNextMessage();

          // keep reading received messages until we encounter the one we care about (with request_seq set to the above
          // reques) or until messages are exhausted.
          while (m_Messages.Count > 0)
          {
            string bodyStr = m_Messages.Dequeue();
            JObject? actual;
            try
            {
              actual = JObject.Parse(bodyStr);
            }
            catch (JsonReaderException)
            {
              Assert.Fail($"failed to parse JSON from: {bodyStr}");
              return;
            }
            if (actual == null)
            {
              Assert.Fail($"parsed JSON from received response string: {bodyStr} from unity-dap is null");
              return;
            }

            // we don't care about other types (e.g., events)
            var _type = (string?)actual["type"];
            if (_type != "response")
              continue;

            var _requestSeq = (int?)actual["request_seq"];
            if (_requestSeq == null)
            {
              Assert.Fail($"request_seq attribute is null (from parsed json: {actual})");
              return;
            }

            if (_requestSeq != item.Key)
            {
              Assert.Fail($"reponse to unexpected request_seq (expected: {item.Key}; got response to: {_requestSeq})");
              return;
            }

            TestContext.Progress.WriteLine($"got response to request_seq: {_requestSeq} {actual.ToString(Formatting.None)}");

            // fetch the response from the stored m_Responses from log.txt
            var expected = m_Responses[_requestSeq.Value];
            if (expected == null)
            {
              Assert.Fail($"could not find expected response in log responses (request_seq = {_requestSeq.Value})");
              return;
            }

            Assert.That(actual, Is.EqualTo(expected).Using(m_Comparer));
            m_Messages.Clear();

            gotResponse = true;
            break;

          }  // END MESSAGES WHILE

        }  // END STDOUT.READ WHILE

      }  // END REQUESTS WHILE
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


  public class DapResponseComparer : IEqualityComparer<JObject>
  {
    public bool Equals(JObject? o1, JObject? o2)
    {

      if (o1 == null && o2 == null)
        return true;

      if (o1 == null || o2 == null)
        return false;

      // protocol message attributes (we ignore Seq as it may change - E.g., when we have different number of threads
      // and therefore events sent for each thread).
      if ((string?)o1["type"] != (string?)o2["type"])
        return false;

      if ((int?)o1["request_seq"] != (int?)o2["request_seq"])
        return false;

      if ((bool?)o1["success"] != (bool?)o2["success"])
        return false;

      if ((string?)o1["command"] != (string?)o2["command"])
        return false;

      if ((string?)o1["message"] != (string?)o2["message"])
        return false;

      // it's really hard to test the threads command because, again, it varies depending on the runtime conditions
      // (you get different response each time you run this test which is not ideal for testing)
      if ((string?)o1["command"] == "threads")
        return true;

      // for all other commands, simlpy compare the bodies
      return JToken.DeepEquals(o1["body"], o2["body"]);
    }

    public int GetHashCode(JObject o) => o.GetHashCode();
  }
}


