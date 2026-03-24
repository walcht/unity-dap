using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using Mono.Debugging.Client;
using Mono.Debugging.Soft;
using Newtonsoft.Json.Linq;

namespace UnityDebugAdapter
{
  internal class UnityDebugSession : DebugSession
  {
    public class UnityDebuggerSession : SoftDebuggerSession
    {
      protected override void OnExit()
      {
        Detach();
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

      // these are commented because they absolutely flood the REPL for front-end DAP clients
      // m_Session.LogWriter = (isStdErr, text) =>
      // {
      //   SendOutput(isStdErr ? "stderr" : "stdout", text);
      // };
      //
      //
      // m_Session.OutputWriter = (isStdErr, text) =>
      // {
      //   SendOutput(isStdErr ? "stderr" : "stdout", text);
      // };

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

      Logger.LogInfo("done constructing UnityDebugSession");
    }

    public Mono.Debugging.Client.StackFrame Frame { get; set; }

    public override void Initialize(int reqSeq, JToken args)
    {

      var initilizeReqArgs = args.ToObject<InitializeRequestArguments>();
      _clientLinesStartAt1 = initilizeReqArgs.linesStartAt1 ?? true;

      var pathFormat = initilizeReqArgs.pathFormat;
      if (pathFormat != null)
      {
        switch (pathFormat)
        {
          case "uri":
            _clientPathsAreURI = true;
            break;
          case "path":
            _clientPathsAreURI = false;
            break;
          default:
            SendErrorResponse(reqSeq, "initialize", 1015, "initialize: bad value '{_format}' for pathFormat",
                new Dictionary<string, string> { { "_format", pathFormat } });
            return;
        }
      }

      var os = Environment.OSVersion;
      if (os.Platform != PlatformID.MacOSX && os.Platform != PlatformID.Unix && os.Platform != PlatformID.Win32NT)
      {
        SendErrorResponse(reqSeq, "initialize", 3000, "Mono Debug is not supported on this platform ({_platform}).",
            new Dictionary<string, string> { { "_platform", os.Platform.ToString() } }, true, true);
        return;
      }

      SendOutput("stdout", "UnityDebug: Initializing");

      var capabilities = new Capabilities()
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
      };
      var response = new Response()
      {
        command = "initialize",
        request_seq = reqSeq,
        success = true,
        body = capabilities,
      };
      SendMessage(response);

      // Mono Debug is ready to accept breakpoints immediately
      SendEvent(new InitializedEvent());
    }

    public override void Launch(int reqSeq, JToken args)
    {
      AttachInternal(args);
      var response = new Response()
      {
        command = "launch",
        request_seq = reqSeq,
        success = true,
      };
      SendMessage(response);
    }

    public override void Attach(int reqSeq, JToken args)
    {
      AttachInternal(args);
      var response = new Response()
      {
        command = "attach",
        request_seq = reqSeq,
        success = true,
      };
      SendMessage(response);
    }

    void AttachInternal(JToken args)
    {
      var attachArgs = args.ToObject<LaunchRequestArguments>();

      if (attachArgs.address == null)
      {
        Logger.LogError("expected \"address\" property string in attach's arguments request");
        return;
      }
      IPAddress address = IPAddress.Parse(attachArgs.address);
      ushort port;
      if (attachArgs.port == null)
      {
        Logger.LogError("expected \"port\" property int with a valid port in attach's arguments request");
        return;
      }
      port = attachArgs.port.Value;

      SetExceptionBreakpoints(attachArgs.__exceptionOptions);

      Connect(address, port);

      SendOutput("stdout", $"UnityDebugAdapter: attached to Unity Mono runtime endpoint via {address}:{port}");
    }


    void Connect(IPAddress address, int port)
    {
      Logger.LogInfo($"connecting to: {address}:{port}");
      lock (m_Lock)
      {
        var startArgs = new SoftDebuggerConnectArgs(string.Empty, address, port)
        {
          MaxConnectionAttempts = MAX_CONNECTION_ATTEMPTS,
          TimeBetweenConnectionAttempts = CONNECTION_ATTEMPT_INTERVAL
        };

        m_Session.Run(new SoftDebuggerStartInfo(startArgs), m_DebuggerSessionOptions);

        m_DebuggeeExecuting = true;
      }
    }

    void SetExceptionBreakpoints(ExceptionOptions[] exceptionOptions)
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

      foreach (ExceptionOptions exception in exceptionOptions)
      {
        string exName = null;
        string exBreakMode = exception.breakMode;

        var path = exception.path?[0];
        if (path.names != null && path.names.Length > 0)
          exName = path.names[0];

        if (exName != null && exBreakMode == "always")
          m_Catchpoints.Add(m_Session.Breakpoints.AddCatchpoint(exName));
      }
    }

    public override void Disconnect(int reqSeq, JToken args)
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
      var response = new Response()
      {
        command = "disconnect",
        request_seq = reqSeq,
        success = true,
      };
      SendMessage(response);

      // as per the specification, the debug adapter should terminate itself
      DebuggerKill();
      Environment.Exit(0);
    }

    public override void SetFunctionBreakpoints(int reqSeq, JToken args)
    {
      var breakpoints = new List<Breakpoint>();
      var response = new Response()
      {
        command = "setFunctionBreakpoints",
        request_seq = reqSeq,
        success = true,
        body = new SetFunctionBreakpointsBody(breakpoints.ToArray())
      };
      SendMessage(response);
    }

    public override void Continue(int reqSeq, JToken args)
    {
      WaitForSuspend();
      var response = new Response()
      {
        command = "continue",
        request_seq = reqSeq,
        success = true,
        body = new ContinueResponseBody()
      };
      SendMessage(response);
      lock (m_Lock)
      {
        if (m_Session == null || m_Session.IsRunning || m_Session.HasExited) return;

        m_Session.Continue();
        m_DebuggeeExecuting = true;
      }
    }

    public override void Next(int reqSeq, JToken args)
    {
      WaitForSuspend();
      var response = new Response()
      {
        command = "next",
        request_seq = reqSeq,
        success = true,
      };
      SendMessage(response);
      lock (m_Lock)
      {
        if (m_Session == null || m_Session.IsRunning || m_Session.HasExited) return;

        m_Session.NextLine();
        m_DebuggeeExecuting = true;
      }
    }

    public override void StepIn(int reqSeq, JToken args)
    {
      WaitForSuspend();
      var response = new Response()
      {
        command = "stepIn",
        request_seq = reqSeq,
        success = true,
      };
      SendMessage(response);
      lock (m_Lock)
      {
        if (m_Session == null || m_Session.IsRunning || m_Session.HasExited) return;

        m_Session.StepLine();
        m_DebuggeeExecuting = true;
      }
    }

    public override void StepOut(int reqSeq, JToken args)
    {
      WaitForSuspend();
      var response = new Response()
      {
        command = "stepOut",
        request_seq = reqSeq,
        success = true,
      };
      SendMessage(response);
      lock (m_Lock)
      {
        if (m_Session == null || m_Session.IsRunning || m_Session.HasExited) return;

        m_Session.Finish();
        m_DebuggeeExecuting = true;
      }
    }

    public override void Pause(int reqSeq, JToken args)
    {
      var response = new Response()
      {
        command = "pause",
        request_seq = reqSeq,
        success = true,
      };
      SendMessage(response);
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

    protected override void SetVariable(int reqSeq, JToken args)
    {
      var reference = (int)args["variablesReference"];
      if (reference == -1)
      {
        SendErrorResponse(reqSeq, "setVariable", 3009, "variables: property 'variablesReference' is missing",
            null, false, true);
        return;
      }

      var value = (string)args["value"];
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
            if (variable.name == (string)args["name"])
            {
              v.Value = value;
              var response = new Response()
              {
                command = "setVariable",
                request_seq = reqSeq,
                success = true,
                body = new SetVariablesResponseBody(value, variable.type, variable.variablesReference),
              };
              SendMessage(response);
            }
          }
        }
      }
    }

    public override void SetExceptionBreakpoints(int reqSeq, JToken args)
    {
      var _args = args.ToObject<SetExceptionBreakpointsArguments>();
      SetExceptionBreakpoints(_args.exceptionOptions);
      var response = new Response()
      {
        command = "setExceptionBreakpoints",
        request_seq = reqSeq,
        success = true,
      };
      SendMessage(response);
    }

    public override void SetBreakpoints(int reqSeq, JToken args)
    {
      string path = null;
      var _args = args.ToObject<SetBreakpointsArguments>();
      var response = new Response()
      {
        command = "setBreakpoints",
        request_seq = reqSeq,
        success = true,
      };

      if (_args.source != null)
      {
        var p = _args.source.path;
        if (p != null && p.Trim().Length > 0)
          path = p;
      }

      if (path == null)
      {
        SendErrorResponse(reqSeq, "setBreakpoints", 3010, "setBreakpoints: property 'source' is empty or misformed",
            null, false, true);
        return;
      }

      if (!HasMonoExtension(path))
      {
        // we only support breakpoints in files mono can handle
        response.body = new SetBreakpointsResponseBody();
        SendMessage(response);
        return;
      }

      SourceBreakpoint[] newBreakpoints = _args.breakpoints ?? Array.Empty<SourceBreakpoint>();
      bool sourceModified = _args.sourceModified ?? false;
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

      var responseBreakpoints = new List<Breakpoint>();
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
            responseBreakpoints.Add(new Breakpoint(true, breakpoint.line, breakpoint.column, breakpoint.logMessage));
          }
          catch (Exception e)
          {
            Logger.LogError($"SetBreakpoints error: msg: {e.Message}, stacktrace: {e.StackTrace}");
            SendErrorResponse(reqSeq, "setBreakpoints", 3011, "setBreakpoints: " + e.Message,
                null, false, true);
            responseBreakpoints.Add(new Breakpoint(false, breakpoint.line, breakpoint.column, e.Message));
          }
        }
        else
        {
          dictionary[breakpoint.line].ConditionExpression = breakpoint.condition;
          responseBreakpoints.Add(new Breakpoint(true, breakpoint.line, breakpoint.column, breakpoint.logMessage));
        }
      }

      response.body = new SetBreakpointsResponseBody(responseBreakpoints);
      SendMessage(response);
    }

    public override void StackTrace(int reqSeq, JToken args)
    {
      var _args = args.ToObject<StackTraceArguments>();
      int maxLevels = _args.levels ?? 10;
      int startFrame = _args.startFrame ?? 0;
      int threadReference = _args.threadId;

      WaitForSuspend();

      ThreadInfo thread = DebuggerActiveThread();
      if (thread.Id != threadReference)
      {
        // Console.Error.WriteLine("stackTrace: unexpected: active thread should be the one requested");
        thread = FindThread(threadReference);
        thread?.SetActive();
      }

      var stackFrames = new List<StackFrame>();
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
          stackFrames.Add(new StackFrame(frameHandle, name, source, ConvertDebuggerLineToClient(line), 0, hint));
        }
      }

      var response = new Response()
      {
        command = "stackTrace",
        request_seq = reqSeq,
        success = true,
        body = new StackTraceResponseBody(stackFrames, totalFrames),
      };
      SendMessage(response);
    }

    ThreadInfo DebuggerActiveThread()
    {
      lock (m_Lock)
      {
        return m_Session?.ActiveThread;
      }
    }

    public override void Source(int reqSeq, JToken args)
    {
      SendErrorResponse(reqSeq, "source", 1020, "No source available");
    }

    public override void Scopes(int reqSeq, JToken args)
    {
      var _args = args.ToObject<ScopesArguments>();

      int frameId = _args.frameId;
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

      var response = new Response()
      {
        command = "scopes",
        request_seq = reqSeq,
        success = true,
        body = new ScopesResponseBody(scopes),
      };
      SendMessage(response);
    }

    public override void Variables(int reqSeq, JToken args)
    {
      var _args = args.ToObject<VariablesArguments>();
      int reference = _args.variablesReference;
      if (reference == -1)
      {
        SendErrorResponse(reqSeq, "variables", 3009,
            "variables: property 'variablesReference' is missing", null, false, true);
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


      var response = new Response()
      {
        command = "variables",
        request_seq = reqSeq,
        success = true,
        body = new VariablesResponseBody(variables),
      };
      SendMessage(response);
    }

    public override void Threads(int reqSeq, JToken args)
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

      var response = new Response()
      {
        command = "threads",
        request_seq = reqSeq,
        success = true,
        body = new ThreadsResponseBody(threads),
      };
      SendMessage(response);
    }

    public override void Evaluate(int reqSeq, JToken args)
    {
      var _args = args.ToObject<EvaluateArguments>();
      var expression = _args.expression;
      var frameId = _args.frameId ?? 0;

      if (expression == null)
      {
        SendErrorResponse(reqSeq, "evaluate", 3014, "Evaluate request failed ({_reason}).",
            new Dictionary<string, string> { { "_reason", "expression missing" } });
        return;
      }

      var frame = m_FrameHandles.Get(frameId, null);
      if (frame == null)
      {
        SendErrorResponse(reqSeq, "evaluate", 3014, "Evaluate request failed ({_reason}).",
            new Dictionary<string, string> { { "_reason", "no active stackframe" } });
        return;
      }

      if (!frame.ValidateExpression(expression))
      {
        SendErrorResponse(reqSeq, "evaluate", 3014, "Evaluate request failed ({_reason}).",
            new Dictionary<string, string> { { "_reason", "invalid expression" } });
        return;
      }

      var response = new Response()
      {
        command = "evaluate",
        request_seq = reqSeq,
        success = true,
      };

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

        response.body = new EvaluateResponseBody(error);
        SendMessage(response);
        return;
      }

      if (flags.HasFlag(ObjectValueFlags.Unknown))
      {
        response.body = new EvaluateResponseBody("invalid expression");
        SendMessage(response);
        return;
      }

      if (flags.HasFlag(ObjectValueFlags.Object) && flags.HasFlag(ObjectValueFlags.Namespace))
      {
        response.body = new EvaluateResponseBody("not available");
        SendMessage(response);
        return;
      }

      int handle = 0;
      if (val.HasChildren)
      {
        handle = m_VariableHandles.Create(val.GetAllChildren());
      }

      response.body = new EvaluateResponseBody(val.DisplayValue, handle);
      SendMessage(response);
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
      return thr?.Backtrace;
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
