# SVoice Windows patches

This directory vendors `hotkey_manager_windows` 0.2.0 under its MIT license.

The SVoice patch:

- adds `MOD_NOREPEAT` so holding a shortcut produces one toggle;
- propagates `RegisterHotKey` failures to Dart instead of reporting success;
- makes unregistering a missing shortcut safe; and
- releases registered shortcuts and the window-procedure delegate on shutdown.
