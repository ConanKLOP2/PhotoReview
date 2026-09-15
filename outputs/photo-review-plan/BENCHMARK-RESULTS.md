# Benchmark results

This file is reserved for measured runs on the local machine. Raw reports are kept beside the application data under the benchmark result directory and are not checked into source control by default.

Required evidence for a release run:

| Field | Required |
|---|---|
| Run ID and UTC time | Yes |
| Profile and workload | Yes |
| Folder file count and total bytes | Yes |
| Iterations and warmup count | Yes |
| P50/P95/P99/max | Yes |
| Cache hits/misses and bytes read | Yes when available |
| Working set and memory pressure | Yes when available |
| Correctness and error stage | Yes |
| Action navigation-before-operation evidence | Action profiles |

Results must state whether the folder was cold or warm and whether the profile ran read-only or against temporary action copies.

