Nothing changes for an application that does not use them. Each .NET family is
a compile error until the project suppresses its ID
([experimental APIs](https://github.com/Runic-Artifex/runic-sdk/blob/v0.21.0/docs/experimental.md#suppressing-a-diagnostic)),
and an npm API is exported only from its package's `./experimental` entry.
Hosted applications (`RUNICDESK103`), the side panel (`RUNICDESK104`) and the
close drain (`RUNICDESK105`) stay experimental in 0.21, with these changes from
the API review:
