- On Windows, the Bridge generator no longer fails a build now and then with
  "Access to the path is denied" or "being used by another process". That
  happened when an editor, an antivirus scan or an indexer held a generated
  file open just as the generator replaced it. The generator now retries the
  replacement for up to about 0.6 seconds before it reports the error.
