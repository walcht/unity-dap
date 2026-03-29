# unity-debug-adapter End-to-End Tests

Currently we are only running a single end-to-end test that involves:

1. Instantiating a Unity Editor instance with the project `./unity_test_project`
opened.

1. Parsing requests from `./log.txt` and forwarding them to unity-dap then
asserting the responses with the responses in the `./log.txt`.

1. Initially, I wanted this test to be trully end-to-end by running a Neovim
front-end DAP client (nvim-dap), running this program (unity-dap), and running
a Unity Editor project instance. That prooved to be very difficult to achieve
(especially running Neovim dap front-end) hence why I chose this approach
instead.

Requirements:

- **Unity 2022.3.X LTS**
- **dotnet >= 9.0** (haven't tested for other versions but should probably
workd).

It is not trivial to fully test the DAP on an actual Unity Editor/Player session
because certain DAP responses depend on the factors that are outside the control
of the debugger (Mono debugger), the dap, and the test runner(s) (e.g., request
for threads may not always return the same response).

I don't have the time to implement a fine-grained test system, so for the
moment this suffices (alongside Unit tests).
