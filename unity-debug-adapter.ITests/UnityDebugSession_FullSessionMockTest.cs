using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace unity_debug_adapter.ITests
{
  [TestFixture]
  public class UnityDebugSession_FullSessionMockTest
  {
    // private static readonly Regex UNITY_VERSION_REGEX = new Regex(@"(\d+)\.(\d+)");
    private Process m_UnityProcess;

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

      Trace.TraceInformation($"Unity executable is set to {unity_exe}");

      // start debuggee (i.e., Unity) on the unity_test_project
      m_UnityProcess = new Process();
      m_UnityProcess.StartInfo.FileName = unity_exe;  // -batchmode -nographics -wait-for-managed-debugger 
      m_UnityProcess.StartInfo.Arguments = $"-projectPath {Path.GetFullPath("./unity_test_project")}";
      m_UnityProcess.StartInfo.WindowStyle = ProcessWindowStyle.Normal;
      m_UnityProcess.StartInfo.CreateNoWindow = false;
      m_UnityProcess.StartInfo.UseShellExecute = false;
      m_UnityProcess.StartInfo.RedirectStandardOutput = true;
      m_UnityProcess.StartInfo.RedirectStandardError = true;
      m_UnityProcess.StartInfo.RedirectStandardInput = true;
      m_UnityProcess.Start();

      Trace.TraceInformation($"started process: {m_UnityProcess.StartInfo.FileName} {m_UnityProcess.StartInfo.Arguments}");

      m_UnityProcess.WaitForExit();

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

      Trace.TraceInformation("Unity process killed successfully");

      Trace.Flush();
      // close Unity Editor
      // close logger
    }
  }
}


