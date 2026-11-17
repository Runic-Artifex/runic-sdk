- The train gate and the train feed select upstream CI runs by commit, never "the
  newest": `run@<commit>` takes the newest successful `main` run at or after that
  commit, the rows T, P-tr and P-sdk refuse a bare `run`, and the train feed reads
  the upstream commit from `TrainCommit` on the train's `PackageVersion`. The gate
  refuses a Translations or Command Line candidate whose `main` moved since the
  runic-sdk run's train feed served it, unless every package is byte-identical.
  `eng/train/README.md` describes the train's choreography step by step.
