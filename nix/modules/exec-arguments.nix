# A systemd ExecStart= command line that passes each argument unchanged: double-quoted, with backslashes, quotes and
# line breaks escaped, and % (specifiers) and $ (variables) doubled.
lib: arguments:
lib.concatMapStringsSep " " (
  argument:
  "\""
  + lib.replaceStrings [ "\\" "\"" "\n" "%" "$" ] [ "\\\\" "\\\"" "\\n" "%%" "$$" ] argument
  + "\""
) arguments
