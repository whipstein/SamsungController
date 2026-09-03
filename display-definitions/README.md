# Display definitions

This catalog is for shareable `*.display.json` definitions. A display definition
selects connection defaults and references a menu definition; it never embeds
menu topology or display-verification evidence.

Personal definitions created in the web interface are stored in the per-user
application-data `display-definitions` directory instead of this repository.
Omit `connection.host` before sharing a definition that should not publish a
private network address.

See [`docs/display-definitions.md`](../docs/display-definitions.md) for the file
format and resolution rules.
