# RAW task progress files

One file per task, `RAW-<NN>.md`, created by the task's agent in its first commit from the template in
[AGENT-PROTOCOL.md §3](../AGENT-PROTOCOL.md) and updated in every commit. It is the handoff: a fresh agent
resumes from its **Next action** after checking it against `git log` and a build.

Only the owning task edits its file. Files are deleted in RAW-70 (git history keeps them).
