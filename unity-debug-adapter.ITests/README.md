# unity-debug-adapter Tests

Currently we are only running a single integration tests that involves running
an nvim-dap frontend (via Neovim server instance) on a simply C# scipt that is
part of a Unity project.

Requirements:

- Neovim >= 0.11 with nvim-dap plugin client installed
- Unity >= 2019.4
