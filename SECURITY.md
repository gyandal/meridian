# Security policy

## Supported versions

Meridian is in preview. Security fixes are made on `main` and released in the next preview; older
previews are not patched.

## Reporting a vulnerability

Please **don't** open a public issue. Report it privately through GitHub:
**[Report a vulnerability](https://github.com/gyandal/meridian/security/advisories/new)** (the
repository's *Security* tab → *Report a vulnerability*).

Include what you found, how to reproduce it, and the impact you expect. You'll get an acknowledgement
within a few days, and we'll agree a fix and disclosure timeline with you. Credit is given in the
advisory unless you'd rather not be named.

## Scope notes

Meridian runs inside your application and queries your data stores. Some areas to keep in mind:

- `DuckDbSourceOptions.Relation` and the column names in source options are SQL fragments from
  **trusted configuration** — never build them from user input.
- Entity ids are numeric and inlined; metric names and time bounds are passed as parameters.
- The sample REST host (`Meridian.Hosts.Http`) is a demo, not a hardened service: it has no
  authentication.
