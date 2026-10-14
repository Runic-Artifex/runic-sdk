# Values for systemd unit settings that pass each word unchanged: double-quoted, with backslashes, quotes and line
# breaks escaped and % (specifiers) doubled. ExecStart= also expands $ (variables), so that is doubled there.
lib:
let
  quote =
    extra: value:
    "\""
    + lib.replaceStrings ([ "\\" "\"" "\n" "%" ] ++ lib.attrNames extra) (
      [ "\\\\" "\\\"" "\\n" "%%" ] ++ lib.attrValues extra
    ) value
    + "\"";
in
{
  # An ExecStart= command line.
  execArguments = arguments: lib.concatMapStringsSep " " (quote { "$" = "$$"; }) arguments;
  # One Environment= assignment, KEY=value as one word.
  environment = name: value: quote { } "${name}=${value}";
}
