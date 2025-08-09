#pragma warning disable IDE1006, IDE0003, IDE0038, IDE0001, IDE0031

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using Mono.Debugging.Client;
using Mono.Debugging.Soft;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityDebugAdapter
{
  internal class UnityDebugSession : DebugSession
  {
    public class UnityDebuggerSession : SoftDebuggerSession
    {
      protected override void OnExit()
      {
        this.Detach();
      }
    }

    readonly string[] MONO_EXTENSIONS = {
            ".cs", ".csx",
            ".cake",
            ".fs", ".fsi", ".ml", ".mli", ".fsx", ".fsscript",
            ".hx"
        };
    const int MAX_CHILDREN = 100;
    const int MAX_CONNECTION_ATTEMPTS = 10;
    const int CONNECTION_ATTEMPT_INTERVAL = 500;

    readonly AutoResetEvent m_ResumeEvent;
    bool m_DebuggeeExecuting;
    readonly object m_Lock = new object();
    SoftDebuggerSession m_Session;
    ProcessInfo m_ActiveProcess;
    Dictionary<string, Dictionary<int, Mono.Debugging.Client.Breakpoint>> m_Breakpoints;
    readonly List<Catchpoint> m_Catchpoints;
    readonly DebuggerSessionOptions m_DebuggerSessionOptions;

    readonly Handles<ObjectValue[]> m_VariableHandles;
    readonly Handles<Mono.Debugging.Client.StackFrame> m_FrameHandles;
    ObjectValue m_Exception;
    readonly Dictionary<int, Thread> m_SeenThreads;
    bool m_Terminated;

    public UnityDebugSession()
    {
      Logger.LogInfo("constructing UnityDebugSession");
      m_ResumeEvent = new AutoResetEvent(false);
      m_Breakpoints = new Dictionary<string, Dictionary<int, Mono.Debugging.Client.Breakpoint>>();
      m_VariableHandles = new Handles<ObjectValue[]>();
      m_FrameHandles = new Handles<Mono.Debugging.Client.StackFrame>();
      m_SeenThreads = new Dictionary<int, Thread>();

      m_DebuggerSessionOptions = new DebuggerSessionOptions
      {
        EvaluationOptions = EvaluationOptions.DefaultOptions
      };

      m_Session = new UnityDebuggerSession
      {
        Breakpoints = new BreakpointStore()
      };

      m_Catchpoints = new List<Catchpoint>();

      m_Session.ExceptionHandler = ex =>
      {
        return true;
      };

      m_Session.LogWriter = (isStdErr, text) =>
      {
        SendOutput(isStdErr ? "stderr" : "stdout", text);
      };

      m_Session.TargetStopped += (sender, e) =>
      {
        if (e.Backtrace != null)
        {
          Frame = e.Backtrace.GetFrame(0);
        }
        else
        {
          SendOutput("stdout", "e.Bracktrace is null");
        }

        Stopped();
        SendEvent(CreateStoppedEvent("step", e.Thread));
        m_ResumeEvent.Set();
      };

      m_Session.TargetHitBreakpoint += (sender, e) =>
      {
        Frame = e.Backtrace.GetFrame(0);
        Stopped();
        SendEvent(CreateStoppedEvent("breakpoint", e.Thread));
        m_ResumeEvent.Set();
      };

      m_Session.TargetExceptionThrown += (sender, e) =>
      {
        Frame = e.Backtrace.GetFrame(0);
        for (var i = 0; i < e.Backtrace.FrameCount; i++)
        {
          if (!e.Backtrace.GetFrame(i).IsExternalCode)
          {
            Frame = e.Backtrace.GetFrame(i);
            break;
          }
        }

        Stopped();
        var ex = DebuggerActiveException();
        if (ex != null)
        {
          m_Exception = ex.Instance;
          SendEvent(CreateStoppedEvent("exception", e.Thread, ex.Message));
        }

        m_ResumeEvent.Set();
      };

      m_Session.TargetUnhandledException += (sender, e) =>
      {
        Stopped();
        var ex = DebuggerActiveException();
        if (ex != null)
        {
          m_Exception = ex.Instance;
          SendEvent(CreateStoppedEvent("exception", e.Thread, ex.Message));
        }

        m_ResumeEvent.Set();
      };

      m_Session.TargetStarted += (sender, e) =>
      {
      };

      m_Session.TargetReady += (sender, e) =>
      {
        m_ActiveProcess = m_Session.GetProcesses().SingleOrDefault();
      };

      m_Session.TargetExited += (sender, e) =>
      {
        DebuggerKill();

        Terminate("target exited");

        m_ResumeEvent.Set();
      };

      m_Session.TargetInterrupted += (sender, e) =>
      {
        m_ResumeEvent.Set();
      };

      m_Session.TargetEvent += (sender, e) => { };

      m_Session.TargetThreadStarted += (sender, e) =>
      {
        var tid = (int)e.Thread.Id;
        lock (m_SeenThreads)
        {
          m_SeenThreads[tid] = new Thread(tid, e.Thread.Name);
        }

        SendEvent(new ThreadEvent("started", tid));
      };

      m_Session.TargetThreadStopped += (sender, e) =>
      {
        var tid = (int)e.Thread.Id;
        lock (m_SeenThreads)
        {
          m_SeenThreads.Remove(tid);
        }

        SendEvent(new ThreadEvent("exited", tid));
      };

      m_Session.OutputWriter = (isStdErr, text) =>
      {
        SendOutput(isStdErr ? "stderr" : "stdout", text);
      };

      Logger.LogInfo("done constructing UnityDebugSession");
    }

    public Mono.Debugging.Client.StackFrame Frame { get; set; }

    public override void Initialize(Response response, dynamic args)
    {
      var os = Environment.OSVersion;
      if (os.Platform != PlatformID.MacOSX && os.Platform != PlatformID.Unix && os.Platform != PlatformID.Win32NT)
      {
        SendErrorResponse(response, 3000, "Mono Debug is not supported on this platform ({_platform}).", new { _platform = os.Platform.ToString() }, true, true);
        return;
      }

      SendOutput("stdout", "UnityDebug: Initializing");

      SendResponse(response, new Capabilities()
      {
        // This debug adapter does not need the configurationDoneRequest.
        supportsConfigurationDoneRequest = false,

        // This debug adapter does not support function breakpoints.
        supportsFunctionBreakpoints = false,

        // This debug adapter support conditional breakpoints.
        supportsConditionalBreakpoints = true,

        // This debug adapter does support a side effect free evaluate request for data hovers.
        supportsEvaluateForHovers = true,

        supportsExceptionOptions = true,

        supportsHitConditionalBreakpoints = true,

        supportsSetVariable = true,

        // This debug adapter does not support exception breakpoint filters
        exceptionBreakpointFilters = new ExceptionBreakpointsFilter[0]
      });

      // Mono Debug is ready to accept breakpoints immediately
      SendEvent(new InitializedEvent());
    }

    public override void Launch(Response response, dynamic args)
    {
      Attach(response, args);
    }

    public override void Attach(Response response, dynamic args)
    {
      string address_str = GetString(args, "address");
      if (address_str == null)
      {
        Logger.LogError("expected \"address\" property string in attach's arguments request");
        return;
      }
      IPAddress address = IPAddress.Parse(address_str);
      int port = GetInt(args, "port", -1);
      if (port == -1)
      {
        Logger.LogError("expected \"port\" property int with a valid port in attach's arguments request");
        return;
      }

      SetExceptionBreakpoints(args.__exceptionOptions);

      Connect(address, port);

      SendOutput("stdout", $"UnityDebugAdapter: attached to Unity Mono runtime endpoint via {address}:{port}");

      SendResponse(response);
    }

    static string CleanPath(string pathToEditorInstanceJson)
    {
      var osVersion = Environment.OSVersion;
      if (osVersion.Platform == PlatformID.MacOSX || osVersion.Platform == PlatformID.Unix)
      {
        return pathToEditorInstanceJson;
      }

      return pathToEditorInstanceJson.TrimStart('/');
    }

    void Connect(IPAddress address, int port)
    {
      Logger.LogInfo($"connecting to: {address}:{port}");
      lock (m_Lock)
      {
        var args0 = new SoftDebuggerConnectArgs(string.Empty, address, port)
        {
          MaxConnectionAttempts = MAX_CONNECTION_ATTEMPTS,
          TimeBetweenConnectionAttempts = CONNECTION_ATTEMPT_INTERVAL
        };

        m_Session.Run(new SoftDebuggerStartInfo(args0), m_DebuggerSessionOptions);

        m_DebuggeeExecuting = true;
      }
    }

    void SetExceptionBreakpoints(dynamic exceptionOptions)
    {
      if (exceptionOptions == null)
      {
        return;
      }

      // clear all existig catchpoints
      foreach (var cp in m_Catchpoints)
      {
        m_Session.Breakpoints.Remove(cp);
      }

      m_Catchpoints.Clear();

      var exceptions = exceptionOptions.ToObject<dynamic[]>();
      for (var i = 0; i < exceptions.Length; i++)
      {
        var exception = exceptions[i];

        string exName = null;
        string exBreakMode = exception.breakMode;

        if (exception.path != null)
        {
          var paths = exception.path.ToObject<dynamic[]>();
          var path = paths[0];
          if (path.names != null)
          {
            var names = path.names.ToObject<dynamic[]>();
            if (names.Length > 0)
            {
              exName = names[0];
            }
          }
        }

        if (exName != null && exBreakMode == "always")
        {
          m_Catchpoints.Add(m_Session.Breakpoints.AddCatchpoint(exName));
        }
      }
    }

    public override void Disconnect(Response response, dynamic args)
    {
      lock (m_Lock)
      {
        if (m_Session != null)
        {
          m_DebuggeeExecuting = true;
          m_Breakpoints = null;
          m_Session.Breakpoints.Clear();
          m_Session.Continue();
          m_Session.Detach();
          m_Session.Adaptor.Dispose();
          m_Session = null;
        }
      }

      SendOutput("stdout", "UnityDebugAdapter: Disconnected");
      SendResponse(response);
    }

    public override void SetFunctionBreakpoints(Response response, dynamic arguments)
    {
      var breakpoints = new List<UnityDebugAdapter.Breakpoint>();
      SendResponse(response, new SetFunctionBreakpointsBody(breakpoints.ToArray()));
    }

    public override void Continue(Response response, dynamic arguments)
    {
      WaitForSuspend();
      SendResponse(response, new ContinueResponseBody());
      lock (m_Lock)
      {
        if (m_Session == null || m_Session.IsRunning || m_Session.HasExited) return;

        m_Session.Continue();
        m_DebuggeeExecuting = true;
      }
    }

    public override void Next(Response response, dynamic arguments)
    {
      WaitForSuspend();
      SendResponse(response);
      lock (m_Lock)
      {
        if (m_Session == null || m_Session.IsRunning || m_Session.HasExited) return;

        m_Session.NextLine();
        m_DebuggeeExecuting = true;
      }
    }

    public override void StepIn(Response response, dynamic arguments)
    {
      WaitForSuspend();
      SendResponse(response);
      lock (m_Lock)
      {
        if (m_Session == null || m_Session.IsRunning || m_Session.HasExited) return;

        m_Session.StepLine();
        m_DebuggeeExecuting = true;
      }
    }

    public override void StepOut(Response response, dynamic arguments)
    {
      WaitForSuspend();
      SendResponse(response);
      lock (m_Lock)
      {
        if (m_Session == null || m_Session.IsRunning || m_Session.HasExited) return;

        m_Session.Finish();
        m_DebuggeeExecuting = true;
      }
    }

    public override void Pause(Response response, dynamic arguments)
    {
      SendResponse(response);
      PauseDebugger();
    }

    void PauseDebugger()
    {
      lock (m_Lock)
      {
        if (m_Session != null && m_Session.IsRunning)
          m_Session.Stop();
      }
    }

    protected override void SetVariable(Response response, object arguments)
    {
      var reference = GetInt(arguments, "variablesReference", -1);
      if (reference == -1)
      {
        SendErrorResponse(response, 3009, "variables: property 'variablesReference' is missing", null, false, true);
        return;
      }

      var value = GetString(arguments, "value");
      if (m_VariableHandles.TryGet(reference, out var children))
      {
        if (children != null && children.Length > 0)
        {
          if (children.Length > MAX_CHILDREN)
          {
            children = children.Take(MAX_CHILDREN).ToArray();
          }

          foreach (var v in children)
          {
            if (v.IsError)
              continue;
            v.WaitHandle.WaitOne();
            var variable = CreateVariable(v);
            if (variable.name == GetString(arguments, "name"))
            {
              v.Value = value;
              SendResponse(response, new SetVariablesResponseBody(value, variable.type, variable.variablesReference));
            }
          }
        }
      }
    }

    public override void SetExceptionBreakpoints(Response response, dynamic arguments)
    {
      SetExceptionBreakpoints(arguments.exceptionOptions);
      SendResponse(response);
    }

    public override void SetBreakpoints(Response response, dynamic arguments)
    {
      string path = null;

      if (arguments.source != null)
      {
        var p = (string)arguments.source.path;
        if (p != null && p.Trim().Length > 0)
        {
          path = p;
        }
      }

      if (path == null)
      {
        SendErrorResponse(response, 3010, "setBreakpoints: property 'source' is empty or misformed", null, false, true);
        return;
      }

      if (!HasMonoExtension(path))
      {
        // we only support breakpoints in files mono can handle
        SendResponse(response, new SetBreakpointsResponseBody());
        return;
      }

      SourceBreakpoint[] newBreakpoints = getBreakpoints(arguments, "breakpoints");
      bool sourceModified = (bool)arguments.sourceModified;
      var lines = newBreakpoints.Select(bp => bp.line);

      Dictionary<int, Mono.Debugging.Client.Breakpoint> dictionary = null;
      if (m_Breakpoints.ContainsKey(path))
      {
        dictionary = m_Breakpoints[path];
        var keys = new int[dictionary.Keys.Count];
        dictionary.Keys.CopyTo(keys, 0);
        foreach (var line in keys)
        {
          if (!lines.Contains(line) || sourceModified)
          {
            var breakpoint = dictionary[line];
            m_Session.Breakpoints.Remove(breakpoint);
            dictionary.Remove(line);
          }
        }
      }
      else
      {
        dictionary = new Dictionary<int, Mono.Debugging.Client.Breakpoint>();
        m_Breakpoints[path] = dictionary;
      }

      var responseBreakpoints = new List<UnityDebugAdapter.Breakpoint>();
      foreach (var breakpoint in newBreakpoints)
      {
        if (!dictionary.ContainsKey(breakpoint.line))
        {
          try
          {
            var bp = m_Session.Breakpoints.Add(path, breakpoint.line);
            bp.ConditionExpression = breakpoint.condition;
            if (!string.IsNullOrEmpty(breakpoint.logMessage))
            {
              bp.HitAction = HitAction.PrintExpression;
              bp.TraceExpression = breakpoint.logMessage;
            }
            dictionary[breakpoint.line] = bp;
            responseBreakpoints.Add(new UnityDebugAdapter.Breakpoint(true, breakpoint.line, breakpoint.column, breakpoint.logMessage));
          }
          catch (Exception e)
          {
            Logger.LogError($"SetBreakpoints error: msg: {e.Message}, stacktrace: {e.StackTrace}");
            SendErrorResponse(response, 3011, "setBreakpoints: " + e.Message, null, false, true);
            responseBreakpoints.Add(new UnityDebugAdapter.Breakpoint(false, breakpoint.line, breakpoint.column, e.Message));
          }
        }
        else
        {
          dictionary[breakpoint.line].ConditionExpression = breakpoint.condition;
          responseBreakpoints.Add(new UnityDebugAdapter.Breakpoint(true, breakpoint.line, breakpoint.column, breakpoint.logMessage));
        }
      }

      SendResponse(response, new SetBreakpointsResponseBody(responseBreakpoints));
    }

    public override void StackTrace(Response response, dynamic arguments)
    {
      int maxLevels = GetInt(arguments, "levels", 10);
      int startFrame = GetInt(arguments, "startFrame", 0);
      int threadReference = GetInt(arguments, "threadId", 0);

      WaitForSuspend();

      ThreadInfo thread = DebuggerActiveThread();
      if (thread.Id != threadReference)
      {
        // Console.Error.WriteLine("stackTrace: unexpected: active thread should be the one requested");
        thread = FindThread(threadReference);
        if (thread != null)
        {
          thread.SetActive();
        }
      }

      var stackFrames = new List<UnityDebugAdapter.StackFrame>();
      var totalFrames = 0;

      var bt = thread.Backtrace;
      if (bt != null && bt.FrameCount >= 0)
      {
        totalFrames = bt.FrameCount;

        for (var i = startFrame; i < Math.Min(totalFrames, startFrame + maxLevels); i++)
        {
          var frame = bt.GetFrame(i);

          string path = frame.SourceLocation.FileName;

          var hint = "subtle";
          Source source = null;
          if (!string.IsNullOrEmpty(path))
          {
            string sourceName = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(sourceName))
            {
              if (File.Exists(path))
              {
                source = new Source(sourceName, ConvertDebuggerPathToClient(path), 0, "normal");
                hint = "normal";
              }
              else
              {
                source = new Source(sourceName, null, 1000, "deemphasize");
              }
            }
          }

          var frameHandle = m_FrameHandles.Create(frame);
          string name = frame.SourceLocation.MethodName;
          int line = frame.SourceLocation.Line;
          stackFrames.Add(new UnityDebugAdapter.StackFrame(frameHandle, name, source, ConvertDebuggerLineToClient(line), 0, hint));
        }
      }

      SendResponse(response, new StackTraceResponseBody(stackFrames, totalFrames));
    }

    ThreadInfo DebuggerActiveThread()
    {
      lock (m_Lock)
      {
        return m_Session?.ActiveThread;
      }
    }

    public override void Source(Response response, dynamic arguments)
    {
      SendErrorResponse(response, 1020, "No source available");
    }

    public override void Scopes(Response response, dynamic args)
    {
      int frameId = GetInt(args, "frameId", 0);
      var frame = m_FrameHandles.Get(frameId, null);

      var scopes = new List<Scope>();

      if (frame.Index == 0 && m_Exception != null)
      {
        scopes.Add(new Scope("Exception", m_VariableHandles.Create(new[] { m_Exception })));
      }

      var locals = new[] { frame.GetThisReference() }.Concat(frame.GetParameters()).Concat(frame.GetLocalVariables()).Where(x => x != null).ToArray();
      if (locals.Length > 0)
      {
        scopes.Add(new Scope("Local", m_VariableHandles.Create(locals)));
      }

      SendResponse(response, new ScopesResponseBody(scopes));
    }

    public override void Variables(Response response, dynamic args)
    {
      int reference = GetInt(args, "variablesReference", -1);
      if (reference == -1)
      {
        SendErrorResponse(response, 3009, "variables: property 'variablesReference' is missing", null, false, true);
        return;
      }

      WaitForSuspend();
      var variables = new List<Variable>();

      if (m_VariableHandles.TryGet(reference, out var children))
      {
        if (children != null && children.Length > 0)
        {
          var more = false;
          if (children.Length > MAX_CHILDREN)
          {
            children = children.Take(MAX_CHILDREN).ToArray();
            more = true;
          }

          if (children.Length < 20)
          {
            // Wait for all values at once.
            WaitHandle.WaitAll(children.Select(x => x.WaitHandle).ToArray());
            variables.AddRange(from v in children where !v.IsError select CreateVariable(v));
          }
          else
          {
            foreach (var v in children)
            {
              if (v.IsError)
                continue;
              v.WaitHandle.WaitOne();
              variables.Add(CreateVariable(v));
            }
          }

          if (more)
          {
            variables.Add(new Variable("...", null, null));
          }
        }
      }

      SendResponse(response, new VariablesResponseBody(variables));
    }

    public override void Threads(Response response, dynamic args)
    {
      var threads = new List<Thread>();
      var process = m_ActiveProcess;
      if (process != null)
      {
        Dictionary<int, Thread> d;
        lock (m_SeenThreads)
        {
          d = new Dictionary<int, Thread>(m_SeenThreads);
        }

        foreach (var t in process.GetThreads())
        {
          int tid = (int)t.Id;
          d[tid] = new Thread(tid, t.Name);
        }

        threads = d.Values.ToList();
      }

      SendResponse(response, new ThreadsResponseBody(threads));
    }

    public override void Evaluate(Response response, dynamic args)
    {
      var expression = GetString(args, "expression");
      var frameId = GetInt(args, "frameId", 0);

      if (expression == null)
      {
        SendError(response, "expression missing");
        return;
      }

      var frame = m_FrameHandles.Get(frameId, null);
      if (frame == null)
      {
        SendError(response, "no active stackframe");
        return;
      }

      if (!frame.ValidateExpression(expression))
      {
        SendError(response, "invalid expression");
        return;
      }

      var evaluationOptions = m_DebuggerSessionOptions.EvaluationOptions.Clone();
      evaluationOptions.EllipsizeStrings = false;
      evaluationOptions.AllowMethodEvaluation = true;
      var val = frame.GetExpressionValue(expression, evaluationOptions);
      val.WaitHandle.WaitOne();

      var flags = val.Flags;
      if (flags.HasFlag(ObjectValueFlags.Error) || flags.HasFlag(ObjectValueFlags.NotSupported))
      {
        string error = val.DisplayValue;
        if (error.IndexOf("reference not available in the current evaluation context") > 0)
        {
          error = "not available";
        }

        SendResponse(response, new EvaluateResponseBody(error));
        return;
      }

      if (flags.HasFlag(ObjectValueFlags.Unknown))
      {
        SendResponse(response, new EvaluateResponseBody("invalid expression"));
        return;
      }

      if (flags.HasFlag(ObjectValueFlags.Object) && flags.HasFlag(ObjectValueFlags.Namespace))
      {
        SendResponse(response, new EvaluateResponseBody("not available"));
        return;
      }

      int handle = 0;
      if (val.HasChildren)
      {
        handle = m_VariableHandles.Create(val.GetAllChildren());
      }

      SendResponse(response, new EvaluateResponseBody(val.DisplayValue, handle));
    }

    void SendError(Response response, string error)
    {
      SendErrorResponse(response, 3014, "Evaluate request failed ({_reason}).", new { _reason = error });
    }

    //---- private ------------------------------------------

    void SendOutput(string category, string data)
    {
      if (!string.IsNullOrEmpty(data))
      {
        if (data[data.Length - 1] != '\n')
        {
          data += '\n';
        }

        SendEvent(new OutputEvent(category, data));
      }
    }

    void Terminate(string _)
    {
      if (!m_Terminated)
      {
        SendEvent(new TerminatedEvent());
        m_Terminated = true;
      }
    }

    StoppedEvent CreateStoppedEvent(string reason, ThreadInfo ti, string text = null)
    {
      return new StoppedEvent((int)ti.Id, reason, text);
    }

    ThreadInfo FindThread(int threadReference)
    {
      if (m_ActiveProcess != null)
      {
        foreach (var t in m_ActiveProcess.GetThreads())
        {
          if (t.Id == threadReference)
          {
            return t;
          }
        }
      }

      return null;
    }

    void Stopped()
    {
      m_Exception = null;
      m_VariableHandles.Reset();
      m_FrameHandles.Reset();
    }

    /*private Variable CreateVariable(ObjectValue v)
    {
        var pname = String.Format("{0} {1}", v.TypeName, v.Name);
        return new Variable(pname, v.DisplayValue, v.HasChildren ? _variableHandles.Create(v.GetAllChildren()) : 0);
    }*/

    Variable CreateVariable(ObjectValue v)
    {
      var dv = v.DisplayValue;
      if (dv.Length > 1 && dv[0] == '{' && dv[dv.Length - 1] == '}')
      {
        dv = dv.Substring(1, dv.Length - 2);
      }

      return new Variable(v.Name, dv, v.TypeName, v.HasChildren ? m_VariableHandles.Create(v.GetAllChildren()) : 0);
    }

    Backtrace DebuggerActiveBacktrace()
    {
      var thr = DebuggerActiveThread();
      return thr == null ? null : thr.Backtrace;
    }

    ExceptionInfo DebuggerActiveException()
    {
      var bt = DebuggerActiveBacktrace();
      return bt?.GetFrame(0).GetException();
    }

    bool HasMonoExtension(string path)
    {
      return MONO_EXTENSIONS.Any(path.EndsWith);
    }

    static int GetInt(dynamic args, string property, int dflt = 0)
    {
      try
      {
        return (int)args[property];
      }
      catch (Exception)
      {
        // this happens so often that it fills up the log fast => hence the LogDebug and not LogWarn
        Logger.LogDebug($"could not GetInt from dynamic container: {JsonConvert.SerializeObject(args)} at property: {property}");
        // ignore and return default value
      }

      return dflt;
    }

    static string GetString(dynamic args, string property, string dflt = null)
    {
      var s = (string)args[property];
      if (s == null)
      {
        // this happens so often that it fills up the log fast => hence the LogDebug and not LogWarn
        Logger.LogDebug($"could not GetString from dynamic args: {JsonConvert.SerializeObject(args)} at property: {property}");
        return dflt;
      }

      s = s.Trim();
      if (s.Length == 0)
      {
        return dflt;
      }

      return s;
    }

    static SourceBreakpoint[] getBreakpoints(dynamic args, string property)
    {
      JArray jsonBreakpoints = args[property];
      var breakpoints = jsonBreakpoints.ToObject<SourceBreakpoint[]>();
      return breakpoints ?? new SourceBreakpoint[0];
    }

    void DebuggerKill()
    {
      lock (m_Lock)
      {
        if (m_Session != null)
        {
          m_DebuggeeExecuting = true;

          if (!m_Session.HasExited)
            m_Session.Exit();

          m_Session.Dispose();
          m_Session = null;
        }
      }
    }

    void WaitForSuspend()
    {
      if (!m_DebuggeeExecuting) return;

      m_ResumeEvent.WaitOne();
      m_DebuggeeExecuting = false;
    }
  }
}
