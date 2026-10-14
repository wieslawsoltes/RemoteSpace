# Expanded form accessibility

The settings expander retains the standard native theme and header toggle, but
its container has a non-focusable group peer. Its usual expand/collapse provider
and state notifications remain available. The earlier Button control type caused
the Skia semantic adapter to wrap the complete form in an HTML button, including
its nested inputs and comboboxes. Action targeting then treated a disabled local
zoom combobox as part of the enabled outer button despite its own disabled state.
`SettingsExpander` removes that invalid interactive ancestry instead of overriding
the child state or weakening the browser assertion. The browser regression now
also rejects button ancestors above the zoom editor; all original tab mouse hits,
keyboard navigation, enabled-state and edit-retention assertions remain.
