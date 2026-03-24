#pragma warning disable IDE1006, IDE0003

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace UnityDebugAdapter
{
  //////////////////////////////////////////////////////////////////////////////
  /// BASE PROTOCOL
  //////////////////////////////////////////////////////////////////////////////

  /// <summary>
  /// On error (whenever success is false), the body can provide more details.
  /// </summary>
  public class ErrorResponseBody
  {
    /// <summary>
    /// A structured error message.
    /// </summary>
    public Message error { get; }

    public ErrorResponseBody(Message error)
    {
      this.error = error;
    }
  }


  /// <summary>
  /// A debug adapter initiated event.
  /// </summary>
  public class Event : ProtocolMessage
  {
    /// <summary>
    /// Type of event.
    /// </summary>
    [JsonProperty(PropertyName = "event")]
    public string eventType { get; }

    /// <summary>
    /// Event-specific information.
    /// </summary>
    public object body { get; }

    public Event(string type, object bdy = null)
        : base("event")
    {
      eventType = type;
      body = bdy;
    }
  }


  /// <summary>
  /// Base class of requests, responses, and events.
  /// </summary>
  public class ProtocolMessage
  {

    /// <summary>
    /// Sequence number of the message (also known as message ID). The `seq` for
    /// the first message sent by a client or debug adapter is 1, and for each
    /// subsequent message is 1 greater than the previous message sent by that
    /// actor. `seq` can be used to order requests, responses, and events, and to
    /// associate requests with their corresponding responses. For protocol
    /// messages of type `request` the sequence number can be used to cancel the
    /// request.
    /// </summary>
    public int seq;


    /// <summary>
    /// Message type.
    /// Values: 'request', 'response', 'event', etc.
    /// </summary>
    public string type;

    public ProtocolMessage() { }

    public ProtocolMessage(string typ)
    {
      type = typ;
    }

    public ProtocolMessage(string typ, int sq)
    {
      type = typ;
      seq = sq;
    }

    public override string ToString()
    {
      var bodyJson = JsonConvert.SerializeObject(this);
      // print rnrn instead of \r\n\r\n to avoid ugly log output
      string header = string.Format($"Content-Length: {bodyJson.Length}rnrn");
      return header + bodyJson;
    }
  }

  /// <summary>
  /// A client or debug adapter initiated request.
  /// </summary>
  public class Request : ProtocolMessage
  {
    /// <summary>
    /// The command to execute.
    /// </summary>
    public string command = null;

    /// <summary>
    /// Object containing arguments for the command.
    /// </summary>
    public object arguments = new object();

    public Request() { }

    public Request(string cmd, object arg)
        : base("request")
    {
      command = cmd;
      arguments = arg;
    }

    public Request(int id, string cmd, object arg)
        : base("request", id)
    {
      command = cmd;
      arguments = arg;
    }
  }


  /// <summary>
  /// Response for a request.
  /// </summary>
  public class Response : ProtocolMessage
  {

    /// <summary>
    /// Sequence number of the corresponding request.
    /// </summary>
    public int request_seq;


    /// <summary>
    /// Outcome of the request. If true, the request was successful and the `body` attribute may contain
    /// the result of the request. If the value is false, the attribute `message` contains the error in short
    /// form and the `body` may contain additional information (see `ErrorResponse.body.error`).
    /// </summary>
    public bool success;

    /// <summary>
    /// Contains the raw error in short form if `success` is false.
    /// This raw error might be interpreted by the client and is not shown in the  UI.
    /// Some predefined values exist. Values:
    ///   'cancelled': the request was cancelled.
    ///   'notStopped': the request may be retried once the adapter is in a 'stopped' state. etc.
    /// </summary>
    public string message;

    /// <summary>
    /// The command requested.
    /// </summary>
    public string command;

    /// <summary>
    /// Contains request result if success is true and error details if success is false.
    /// </summary>
    public object body;

    public Response() : base("response") { }

    public Response(Request req)
        : base("response")
    {
      success = true;
      request_seq = req.seq;
      command = req.command;
    }

    public void SetBody(object bdy)
    {
      success = true;
      body = bdy;
    }

    public void SetErrorBody(string msg, ErrorResponseBody bdy = null)
    {
      success = false;
      message = msg;
      body = bdy;
    }
  }

  //////////////////////////////////////////////////////////////////////////////
  /// EVENTS
  //////////////////////////////////////////////////////////////////////////////


  /// <summary>
  /// This event indicates that the debug adapter is ready to accept configuration requests (e.g. setBreakpoints,
  /// setExceptionBreakpoints).
  /// </summary>
  public class InitializedEvent : Event
  {
    public InitializedEvent()
        : base("initialized") { }
  }

  public class StoppedEvent : Event
  {
    public StoppedEvent(int tid, string reasn, string txt = null)
        : base("stopped", new
        {
          threadId = tid,
          reason = reasn,
          text = txt,
          allThreadsStopped = true
        })
    { }
  }

  public class ExitedEvent : Event
  {
    public ExitedEvent(int exCode)
        : base("exited", new { exitCode = exCode }) { }
  }

  public class TerminatedEvent : Event
  {
    public TerminatedEvent()
        : base("terminated") { }
  }

  public class ThreadEvent : Event
  {
    public ThreadEvent(string reasn, int tid)
        : base("thread", new
        {
          reason = reasn,
          threadId = tid
        })
    { }
  }

  public class OutputEvent : Event
  {
    public OutputEvent(string cat, string outpt)
        : base("output", new
        {
          category = cat,
          output = outpt
        })
    { }
  }


  //////////////////////////////////////////////////////////////////////////////
  /// REQUESTS and RESPONSES
  //////////////////////////////////////////////////////////////////////////////

  public class InitializeRequestArguments
  {
    /// <summary> The ID of the client using this adapter. </summary>
    public string clientID = null;

    /// <summary> The human-readable name of the client using this adapter. </summary>
    public string clientName = null;

    /// <summary> The ID of the debug adapter. </summary>
    public string adapterID;

    /// <summary> The ISO-639 locale of the client using this adapter, e.g. en-US or de-CH. </summary>
    public string locale = null;

    /// <summary> If true all line numbers are 1-based (default). </summary>
    public bool? linesStartAt1;

    /// <summary> If true all column numbers are 1-based (default). </summary>
    public bool? columnsStartAt1;

    /// <summary>
    /// Determines in what format paths are specified. The default is `path`, which is the native format.
    /// Values: 'path', 'uri', etc.
    /// </summary>
    public string pathFormat = null;

    /// <summary>
    /// Client supports the `type` attribute for variables.
    /// </summary>
    public bool? supportsVariableType;

    /**
     * Client supports the paging of variables.
     */
    public bool? supportsVariablePaging;

    /**
     * Client supports the `runInTerminal` request.
     */
    public bool? supportsRunInTerminalRequest;

    /**
     * Client supports memory references.
     */
    public bool? supportsMemoryReferences;

    /**
     * Client supports progress reporting.
     */
    public bool? supportsProgressReporting;

    /**
     * Client supports the `invalidated` event.
     */
    public bool? supportsInvalidatedEvent;

    /**
     * Client supports the `memory` event.
     */
    public bool? supportsMemoryEvent;

    /**
     * Client supports the `argsCanBeInterpretedByShell` attribute on the
     * `runInTerminal` request.
     */
    public bool? supportsArgsCanBeInterpretedByShell;

    /**
     * Client supports the `startDebugging` request.
     */
    public bool? supportsStartDebuggingRequest;

    /**
     * The client will interpret ANSI escape sequences in the display of
     * `OutputEvent.output` and `Variable.value` fields when
     * `Capabilities.supportsANSIStyling` is also enabled.
     */
    public bool? supportsANSIStyling;
  }


  public class LaunchRequestArguments
  {
    /// <summary> Extension property (not part of DAP). </summary>
    public string address;

    /// <summary> Extension property (not part of DAP). </summary>
    public ushort? port;

    /// <summary> Extension property (not part of DAP). </summary>
    public ExceptionOptions[] __exceptionOptions = null;

    /**
     * If true, the launch request should launch the program without enabling
     * debugging.
     */
    public bool? noDebug;

    /**
     * Arbitrary data from the previous, restarted session.
     * The data is sent as the `restart` attribute of the `terminated` event.
     * The client should leave the data intact.
     */
    public object _restart = null;
  }


  public class DisconnectArguments
  {
    /**
     * A value of true indicates that this `disconnect` request is part of a
     * restart sequence.
     */
    public bool? restart;

    /**
     * Indicates whether the debuggee should be terminated when the debugger is
     * disconnected.
     * If unspecified, the debug adapter is free to do whatever it thinks is best.
     * The attribute is only honored by a debug adapter if the corresponding
     * capability `supportTerminateDebuggee` is true.
     */
    public bool? terminateDebuggee;

    /**
     * Indicates whether the debuggee should stay suspended when the debugger is
     * disconnected.
     * If unspecified, the debuggee should resume execution.
     * The attribute is only honored by a debug adapter if the corresponding
     * capability `supportSuspendDebuggee` is true.
     */
    public bool? suspendDebuggee;
  }

  public class NextArguments
  {
    /**
     * Specifies the thread for which to resume execution for one step (of the
     * given granularity).
     */
    public int threadId;

    /**
     * If this flag is true, all other suspended threads are not resumed.
     */
    public bool? singleThread;

    /**
     * Stepping granularity. If no granularity is specified, a granularity of
     * `statement` is assumed.
     */
    public SteppingGranularity? granularity;
  }

  public class ContinueArguments
  {
    /**
     * Specifies the active thread. If the debug adapter supports single thread
     * execution (see `supportsSingleThreadExecutionRequests`) and the argument
     * `singleThread` is true, only the thread with this ID is resumed.
     */
    public int threadId;

    /**
     * If this flag is true, execution is resumed only for the thread with given
     * `threadId`.
     */
    public bool? singleThread;
  }


  public class StepInArguments
  {
    /**
     * Specifies the thread for which to resume execution for one step-into (of
     * the given granularity).
     */
    public int threadId;

    /**
     * If this flag is true, all other suspended threads are not resumed.
     */
    public bool? singleThread;

    /**
     * Id of the target to step into.
     */
    public int? targetId;

    /**
     * Stepping granularity. If no granularity is specified, a granularity of
     * `statement` is assumed.
     */
    public SteppingGranularity? granularity;
  }


  public class StepOutArguments
  {
    /**
     * Specifies the thread for which to resume execution for one step-out (of the
     * given granularity).
     */
    public int threadId;

    /**
     * If this flag is true, all other suspended threads are not resumed.
     */
    public bool? singleThread;

    /**
     * Stepping granularity. If no granularity is specified, a granularity of
     * `statement` is assumed.
     */
    public SteppingGranularity? granularity;
  }


  public class SetExceptionBreakpointsArguments
  {
    /**
     * Set of exception filters specified by their ID. The set of all possible
     * exception filters is defined by the `exceptionBreakpointFilters`
     * capability. The `filter` and `filterOptions` sets are additive.
     */
    public string[] filters;

    /**
     * Set of exception filters and their options. The set of all possible
     * exception filters is defined by the `exceptionBreakpointFilters`
     * capability. This attribute is only honored by a debug adapter if the
     * corresponding capability `supportsExceptionFilterOptions` is true. The
     * `filter` and `filterOptions` sets are additive.
     */
    public ExceptionFilterOptions[] filterOptions = null;

    /**
     * Configuration options for selected exceptions.
     * The attribute is only honored by a debug adapter if the corresponding
     * capability `supportsExceptionOptions` is true.
     */
    public ExceptionOptions[] exceptionOptions = null;
  }


  public class SetBreakpointsArguments
  {
    /**
     * The source location of the breakpoints; either `source.path` or
     * `source.sourceReference` must be specified.
     */
    public Source source;

    /**
     * The code locations of the breakpoints.
     */
    public SourceBreakpoint[] breakpoints = null;

    /**
     * Deprecated: The code locations of the breakpoints.
     */
    public int[] lines = null;

    /**
     * A value of true indicates that the underlying source has been modified
     * which results in new breakpoint locations.
     */
    public bool? sourceModified;
  }


  public class StackTraceArguments
  {
    /**
     * Retrieve the stacktrace for this thread.
     */
    public int threadId;

    /**
     * The index of the first frame to return; if omitted frames start at 0.
     */
    public int? startFrame;

    /**
     * The maximum number of frames to return. If levels is not specified or 0,
     * all frames are returned.
     */
    public int? levels;

    /**
     * Specifies details on how to format the returned `StackFrame.name`. The
     * debug adapter may format requested details in any way that would make sense
     * to a developer.
     * The attribute is only honored by a debug adapter if the corresponding
     * capability `supportsValueFormattingOptions` is true.
     */
    public StackFrameFormat format = null;
  }


  public class ScopesArguments
  {
    /**
     * Retrieve the scopes for the stack frame identified by `frameId`. The
     * `frameId` must have been obtained in the current suspended state. See
     * 'Lifetime of Object References' in the Overview section for details.
     */
    public int frameId;
  }


  public class VariablesArguments
  {
    /**
     * The variable for which to retrieve its children. The `variablesReference`
     * must have been obtained in the current suspended state. See 'Lifetime of
     * Object References' in the Overview section for details.
     */
    public int variablesReference;

    /**
     * Filter to limit the child variables to either named or indexed. If omitted,
     * both types are fetched.
     * Values: 'indexed', 'named'
     */
    public string filter = null;

    /**
     * The index of the first variable to return; if omitted children start at 0.
     * The attribute is only honored by a debug adapter if the corresponding
     * capability `supportsVariablePaging` is true.
     */
    public int? start;

    /**
     * The number of variables to return. If count is missing or 0, all variables
     * are returned.
     * The attribute is only honored by a debug adapter if the corresponding
     * capability `supportsVariablePaging` is true.
     */
    public int? count;

    /**
     * Specifies details on how to format the Variable values.
     * The attribute is only honored by a debug adapter if the corresponding
     * capability `supportsValueFormattingOptions` is true.
     */
    public ValueFormat format = null;
  }


  public class EvaluateArguments
  {
    /**
     * The expression to evaluate.
     */
    public string expression;

    /**
     * Evaluate the expression in the scope of this stack frame. If not specified,
     * the expression is evaluated in the global scope.
     */
    public int? frameId;

    /**
     * The contextual line where the expression should be evaluated. In the
     * 'hover' context, this should be set to the start of the expression being
     * hovered.
     */
    public int? line;

    /**
     * The contextual column where the expression should be evaluated. This may be
     * provided if `line` is also provided.
     * 
     * It is measured in UTF-16 code units and the client capability
     * `columnsStartAt1` determines whether it is 0- or 1-based.
     */
    public int? column;

    /**
     * The contextual source in which the `line` is found. This must be provided
     * if `line` is provided.
     */
    public Source source = null;

    /**
     * The context in which the evaluate request is used.
     * Values:
     * 'watch': evaluate is called from a watch view context.
     * 'repl': evaluate is called from a REPL context.
     * 'hover': evaluate is called to generate the debug hover contents.
     * This value should only be used if the corresponding capability
     * `supportsEvaluateForHovers` is true.
     * 'clipboard': evaluate is called to generate clipboard contents.
     * This value should only be used if the corresponding capability
     * `supportsClipboardContext` is true.
     * 'variables': evaluate is called from a variables view context.
     * etc.
     */
    public string context = null;

    /**
     * Specifies details on how to format the result.
     * The attribute is only honored by a debug adapter if the corresponding
     * capability `supportsValueFormattingOptions` is true.
     */
    public ValueFormat format = null;
  }











  public class StackTraceResponseBody
  {
    public StackFrame[] stackFrames { get; }
    public int totalFrames { get; }

    public StackTraceResponseBody(List<StackFrame> frames, int total)
    {
      stackFrames = frames.ToArray<StackFrame>();
      totalFrames = total;
    }
  }

  public class ScopesResponseBody
  {
    public Scope[] scopes { get; }

    public ScopesResponseBody(List<Scope> scps)
    {
      scopes = scps.ToArray<Scope>();
    }
  }

  public class VariablesResponseBody
  {
    public Variable[] variables { get; }

    public VariablesResponseBody(List<Variable> vars)
    {
      variables = vars.ToArray<Variable>();
    }
  }

  public class ThreadsResponseBody
  {
    public Thread[] threads { get; }

    public ThreadsResponseBody(List<Thread> ths)
    {
      threads = ths.ToArray<Thread>();
    }
  }

  public class EvaluateResponseBody
  {
    public string result { get; }
    public int variablesReference { get; }

    public EvaluateResponseBody(string value, int reff = 0)
    {
      result = value;
      variablesReference = reff;
    }
  }

  public class SetBreakpointsResponseBody
  {
    public Breakpoint[] breakpoints { get; }

    public SetBreakpointsResponseBody(List<Breakpoint> bpts = null)
    {
      if (bpts == null)
        breakpoints = new Breakpoint[0];
      else
        breakpoints = bpts.ToArray<Breakpoint>();
    }
  }

  public class SetVariablesResponseBody
  {
    public string value { get; }
    public string type { get; }
    public int variablesReference { get; }

    public SetVariablesResponseBody(string value, string type, int variablesReference)
    {
      this.value = value;
      this.type = type;
      this.variablesReference = variablesReference;
    }
  }

  public class ContinueResponseBody
  {
    public bool allThreadsContinued = true;
  }

  public class SetFunctionBreakpointsBody
  {
    public Breakpoint[] breakpoints { get; }

    public SetFunctionBreakpointsBody(Breakpoint[] breakpoints)
    {
      this.breakpoints = breakpoints;
    }
  }

  //////////////////////////////////////////////////////////////////////////////
  /// REVERSE REQUESTS
  //////////////////////////////////////////////////////////////////////////////


  //////////////////////////////////////////////////////////////////////////////
  /// TYPES
  //////////////////////////////////////////////////////////////////////////////

  /// <summary>
  /// Information about a breakpoint created in setBreakpoints, setFunctionBreakpoints, setInstructionBreakpoints, or
  /// setDataBreakpoints requests.
  /// </summary>
  public class Breakpoint
  {
    public int id { get; }
    public bool verified { get; }
    public string message { get; }
    public Source source { get; }
    public int line { get; }
    public int column { get; }
    public int endLine { get; }
    public int endColumn { get; }

    public Breakpoint(bool verified, int line, int column, string logMessage)
    {
      this.verified = verified;
      this.line = line;
      this.column = column;
      this.message = logMessage;
    }
  }

  /// <summary>
  /// Information about the capabilities of a debug adapter.
  /// </summary>
  public class Capabilities
  {
    /// <summary>
    /// The debug adapter supports the `configurationDone` request.
    /// </summary>
    public bool supportsConfigurationDoneRequest = false;

    /// <summary>
    /// The debug adapter supports function breakpoints.
    /// </summary>
    public bool supportsFunctionBreakpoints = false;

    /// <summary>
    /// The debug adapter supports conditional breakpoints.
    /// </summary>
    public bool supportsConditionalBreakpoints = false;

    /// <summary>
    /// The debug adapter supports breakpoints that break execution after a
    /// specified number of hits.
    /// </summary>
    public bool supportsHitConditionalBreakpoints = false;

    /// <summary>
    /// The debug adapter supports a (side effect free) `evaluate` request for data hovers.
    /// </summary>
    public bool supportsEvaluateForHovers = false;

    /// <summary>
    /// Available exception filter options for the `setExceptionBreakpoints` request.
    /// </summary>
    public ExceptionBreakpointsFilter[] exceptionBreakpointFilters = new ExceptionBreakpointsFilter[0];

    /// <summary>
    /// The debug adapter supports setting a variable to a value.
    /// </summary>
    public bool supportsSetVariable = false;

    /// <summary>
    /// The debug adapter supports `exceptionOptions` on the `setExceptionBreakpoints` request.
    /// </summary>
    public bool supportsExceptionOptions = false;

    /// <summary>
    /// The debug adapter supports log points by interpreting the `logMessage` attribute of the `SourceBreakpoint`.
    /// </summary>
    public bool supportsLogPoints = false;
  }


  /// <summary>
  /// An ExceptionBreakpointsFilter is shown in the UI as an filter option for configuring how exceptions are dealt with.
  /// </summary>
  public class ExceptionBreakpointsFilter
  {
    public string filter { get; }
    public string label { get; }

    [JsonProperty("default")]
    public bool? defaultValue { get; }

    public ExceptionBreakpointsFilter(string filter, string label, bool defaultValue = false)
    {
      this.filter = filter;
      this.label = label;
      this.defaultValue = defaultValue;
    }
  }

  public class ExceptionOptions
  {
    /**
     * A path that selects a single or multiple exceptions in a tree. If `path` is
     * missing, the whole tree is selected.
     * By convention the first segment of the path is a category that is used to
     * group exceptions in the UI.
     */
    public ExceptionPathSegment[] path;

    /**
     * Condition when a thrown exception should result in a break.
     */
    // TODO: add ExceptionBreakMode
    public string breakMode;
  }


  public class ExceptionFilterOptions
  {
    /**
     * ID of an exception filter returned by the `exceptionBreakpointFilters`
     * capability.
     */
    public string filterId;

    /**
     * An expression for conditional exceptions.
     * The exception breaks into the debugger if the result of the condition is
     * true.
     */
    public string condition = null;

    /**
     * The mode of this exception breakpoint. If defined, this must be one of the
     * `breakpointModes` the debug adapter advertised in its `Capabilities`.
     */
    public string mode = null;
  }


  public class ExceptionPathSegment
  {
    /**
     * If false or missing this segment matches the names provided, otherwise it
     * matches anything except the names provided.
     */
    public bool? negate;

    /**
     * Depending on the value of `negate` the names that should match or not
     * match.
     */
    public string[] names;
  }


  /// <summary>
  /// A structured message object. Used to return errors from requests.
  /// </summary>
  public class Message
  {
    /// <summary>
    /// Unique (within a debug adapter implementation) identifier for the message. The purpose of these error IDs is to help extension authors that have the requirement that every user visible error message needs a corresponding error number, so that users or customer support can find information about the specific error more easily.
    /// </summary>
    public int id;

    /// <summary>
    /// A format string for the message. Embedded variables have the form `{name}`. If variable name starts with an underscore character, the variable does not contain user data (PII) and can be safely used for telemetry purposes. </summary>
    public string format;

    /// <summary>
    /// An object used as a dictionary for looking up the variables in the format string.
    /// </summary>
    public Dictionary<string, string> variables = null;

    public bool? showUser;

    public bool? sendTelemetry;

    public Message(int id, string format, Dictionary<string, string> variables = null, bool user = true, bool telemetry = false)
    {
      this.id = id;
      this.format = format;
      this.variables = variables;
      this.showUser = user;
      this.sendTelemetry = telemetry;
    }
  }


  /// <summary>
  /// A Scope is a named container for variables. Optionally a scope can map to a source or a range within a source.
  /// </summary>
  public class Scope
  {
    public string name { get; }
    public int variablesReference { get; }
    public bool expensive { get; }

    public Scope(string name, int variablesReference, bool expensive = false)
    {
      this.name = name;
      this.variablesReference = variablesReference;
      this.expensive = expensive;
    }
  }

  /// <summary>
  /// A Source is a descriptor for source code. It is returned from the debug adapter as part of a StackFrame and it is
  /// used by clients when specifying breakpoints.
  /// </summary>
  public class Source
  {
    public string name { get; }
    public string path { get; }
    public int sourceReference { get; }
    public string presentationHint { get; }

    public Source(string name, string path, int sourceReference, string hint)
    {
      this.name = name;
      this.path = path;
      this.sourceReference = sourceReference;
      this.presentationHint = hint;
    }
  }

  /// <summary>
  /// Properties of a breakpoint or logpoint passed to the setBreakpoints request.
  /// </summary>
  public class SourceBreakpoint
  {
    public int line;
    public int column;
    public string condition;
    public string hitCondition;
    public string logMessage;
  }


  /// <summary>
  /// A Stackframe contains the source location.
  /// </summary>
  public class StackFrame
  {
    public int id { get; }
    public Source source { get; }
    public int line { get; }
    public int column { get; }
    public string name { get; }
    public string presentationHint { get; }

    public StackFrame(int id, string name, Source source, int line, int column, string hint)
    {
      this.id = id;
      this.name = name;
      this.source = source;

      // These should NEVER be negative
      this.line = Math.Max(0, line);
      this.column = Math.Max(0, column);

      this.presentationHint = hint;
    }
  }


  public class StackFrameFormat : ValueFormat
  {
    /**
     * Displays parameters for the stack frame.
     */
    public bool? parameters;

    /**
     * Displays the types of parameters for the stack frame.
     */
    public bool? parameterTypes;

    /**
     * Displays the names of parameters for the stack frame.
     */
    public bool? parameterNames;

    /**
     * Displays the values of parameters for the stack frame.
     */
    public bool? parameterValues;

    /**
     * Displays the line number of the stack frame.
     */
    public bool? line;

    /**
     * Displays the module of the stack frame.
     */
    public bool? module;

    /**
     * Includes all stack frames, including those the debug adapter might
     * otherwise hide.
     */
    public bool? includeAll;
  }




  public class StepInTarget
  {
    /**
     * Unique identifier for a step-in target.
     */
    public int id;

    /**
     * The name of the step-in target (shown in the UI).
     */
    public string label;

    /**
     * The line of the step-in target.
     */
    public int? line;

    /**
     * Start position of the range covered by the step in target. It is measured
     * in UTF-16 code units and the client capability `columnsStartAt1` determines
     * whether it is 0- or 1-based.
     */
    public int? column;

    /**
     * The end line of the range covered by the step-in target.
     */
    public int? endLine;

    /**
     * End position of the range covered by the step in target. It is measured in
     * UTF-16 code units and the client capability `columnsStartAt1` determines
     * whether it is 0- or 1-based.
     */
    public int? endColumn;
  }


  public enum SteppingGranularity
  {
    statement,
    line,
    instruction
  }


  /// <summary>
  /// A Thread.
  /// </summary>
  public class Thread
  {
    public int id { get; }
    public string name { get; }

    public Thread(int id, string name)
    {
      this.id = id;
      if (name == null || name.Length == 0)
      {
        this.name = string.Format("Thread #{0}", id);
      }
      else
      {
        this.name = name;
      }
    }
  }


  /// <summary>
  /// A Variable is a name/value pair.
  /// The type attribute is shown if space permits or when hovering over the variable’s name.
  /// The kind attribute is used to render additional properties of the variable, e.g. different icons can be used to
  /// indicate that a variable is public or private.
  /// If the value is structured (has children), a handle is provided to retrieve the children with the variables request. If the number of named or indexed children is large, the numbers should be returned via the namedVariables and indexedVariables attributes. The client can use this information to present the children in a paged UI and fetch them in chunks.
  /// </summary>
  public class Variable
  {
    public string name;
    public string value;
    public string type = null;
    public int variablesReference = 0;

    public Variable(string name, string value, string type, int variablesReference = 0)
    {
      this.name = name;
      this.value = value;
      this.type = type;
      this.variablesReference = variablesReference;
    }
  }


  public class ValueFormat
  {
    /**
     * Display the value in hex.
     */
    public bool? hex;
  }
}

