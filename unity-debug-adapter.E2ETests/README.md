# unity-debug-adapter Tests

Currently we are only running a single end-to-end test that involves:

1. instantiating a Unity Editor instance with the project `./unity_test_project`
opened.
1. parsing requests from `./log.txt` and forwarding them to unity-dap then
asserting the responses with the responses in the `./log.txt`.
1. initially, I wanted this test to be trully end-to-end by running a Neovim
front-end DAP client (nvim-dap), running this program (unity-dap), and running
a Unity Editor project instance. That prooved to be very difficult to achieve
(especially running Neovim dap front-end) hence why I chose this approach
instead.

Requirements:

- Unity >= 2019.4
