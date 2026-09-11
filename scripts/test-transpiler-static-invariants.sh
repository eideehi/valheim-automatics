#!/usr/bin/env bash
#
# Source-shape invariants for how the Automatic Processing transpilers refer to IL locals.
#
# These are regression tripwires, not proofs. Each says that a defect shape has not come
# back: a hardcoded local slot, a CodeMatcher match whose failure goes unreported, a resolver
# that lets an input it cannot resolve through, a transpiler that derives its locals on a path
# of its own. What can be established offline - that an anchor must match exactly once, that a
# derived local must be declared with the expected type, that reordered locals are followed
# rather than assumed, that the released game's method bodies resolve as before - is executed
# by tests/TranspilerLocalsHarness against the same derivation functions the transpilers call.
# Whether the patched game methods then run correctly is only visible in the game.
#
# Every check reads the source with comments and string contents blanked out, and matches
# calls over a whitespace-collapsed stream, so that neither a comment nor a line break or a
# space before a parenthesis changes what a check sees; hits are still reported as file:line.
# The refusal check and the one-path checks only show that a call is written, not what it
# does: that behaviour is covered by the harness, whose resolver and derivation cases fail
# when it breaks.
#
# What a tripwire may never do is fail to run and call that a pass. Every search below
# distinguishes "no match" from "could not search", and a missing file stops the script rather
# than quietly satisfying every rejection in it.
#
set -euo pipefail

failures=0
patches_file="Automatics/AutomaticProcessing/Patches.cs"
locals_file="Automatics/AutomaticProcessing/TranspilerLocals.cs"
mapping_patches_file="Automatics/AutomaticMapping/Patches.cs"
repair_patches_file="Automatics/AutomaticRepair/Patches.cs"
repair_locals_file="Automatics/AutomaticRepair/TranspilerLocals.cs"

# The files whose transpilers are held to these rules. Another module's transpilers join this
# list when they are brought under the same resolution.
transpiler_files=("$patches_file" "$locals_file")

# The files in which every CodeMatcher match must be checked with ThrowIfInvalid. The Automatic
# Mapping transpilers are held to that rule alone: they still write their local slots as numbers,
# which the other rules here reject. tests/TranspilerLocalsHarness checks that their anchors
# match the released game's method bodies.
match_checked_files=("${transpiler_files[@]}" "$mapping_patches_file" "$repair_patches_file")

# The files that must contain at least one CodeMatcher match, so that the ThrowIfInvalid check
# cannot pass for having nothing to look at.
matching_files=("$patches_file" "$mapping_patches_file" "$repair_patches_file")

# The only code that may spell a local slot as a short-form opcode (ldloc.0-3, stloc.0-3) or as
# a literal operand, because encoding and decoding local opcodes is what these helpers are for.
# They are exempted by name, never by line range, and each name must resolve to exactly one
# declaration in TranspilerLocals.cs, so a rename or a copy cannot quietly move the exemption.
exempt_helpers=(IsLocalLoad IsLocalStore LocalIndexOf LoadLocal StoreLocal)

# The resolvers that must refuse what they cannot resolve.
refusing_resolvers=(LocalIndexOf FindSingle RequireLocal)

# Each transpiler whose locals cannot be resolved by type, and the function in TranspilerLocals.cs
# that derives its locals and injection points. tests/TranspilerLocalsHarness executes those
# functions, so anything a transpiler derived on its own would be code no offline check runs.
anchored_transpilers=(
  "Smelter_UpdateSmelter_Transpiler:DeriveSmelterLoop"
  "CookingStation_UpdateFuel_Transpiler:DeriveOvenFuel"
)

# The patterns below are POSIX EREs matched against a stream in which every run of whitespace,
# line breaks included, is a single space; ` *` therefore stands for any whitespace, and a
# leading `(^|[^A-Za-z0-9_])` for a word boundary.

# A local slot written into the source: a short-form local opcode, a local opcode whose operand
# is a number, or the encoders LoadLocal / StoreLocal called with a number (a cast in front of
# the number does not hide it). An operand or argument that is a derived index, such as
# `OpCodes.Ldloc_S, slotIndex` or `LoadLocal(oven.Fuel)`, is not a hardcoded slot.
hardcoded_slot='OpCodes *\. *(Ldloc|Stloc)_[0-3]([^A-Za-z0-9_]|$)|OpCodes *\. *(Ldloc|Stloc)(_S)? *, *(\( *[A-Za-z_.]+ *\) *)?[0-9]|(^|[^A-Za-z0-9_])(LoadLocal|StoreLocal) *\( *(\( *[A-Za-z_.]+ *\) *)?[0-9]'

# OpCodes brought in under another name. With `using static ...OpCodes;` a slot can be written
# as a bare Ldloc_1, and with an alias as X.Ldloc_1; neither is the OpCodes.X spelling the
# hardcoded-slot pattern looks for.
opcodes_import='(^|[^A-Za-z0-9_])using +static +([A-Za-z0-9_.:]*[.:])?OpCodes *;|(^|[^A-Za-z0-9_])using +[A-Za-z_][A-Za-z0-9_]* *= *([A-Za-z0-9_.:]*[.:])?OpCodes *;'

# What only the derivation functions may do - match an anchor, read or check a local, read the
# declared local types - and what only InsertBackToFront may do: place instructions.
own_resolution='(^|[^A-Za-z0-9_])(FindSingle|RequireLocal|RequireSameLocal|LocalAt|LocalIndexOf|DeclaredLocalTypes|SingleLocalOfType|GetSingleLocalIndex|GetMethodBody) *\(|\. *Insert(Range)? *\('

fail() {
  printf 'FAIL: %s\n' "$*" >&2
  failures=$((failures + 1))
}

# Prints the number of matching lines in the text on stdin, or the word ERROR, which no caller
# accepts. rg exits 0 on a match, 1 on no match, and 2 or more on an error; collapsing the last
# two is how a check silently stops checking.
count_stdin_matches() {
  local pattern="$1"
  local status=0
  local count=""

  count="$(rg -c "$pattern")" || status=$?
  case "$status" in
    0) printf '%s\n' "$count" ;;
    1) printf '0\n' ;;
    *) printf 'ERROR\n' ;;
  esac
}

# True when any argument is the ERROR marker from first_match_line. ERROR is not a number, and
# bash arithmetic would read it as 0, so it has to be caught before any comparison.
lookup_failed() {
  local value

  for value in "$@"; do
    if [[ "$value" == "ERROR" ]]; then
      return 0
    fi
  done

  return 1
}

# Prints the 1-based line number of the first match in the text on stdin, nothing when there is
# no match, and ERROR when the search could not run. `rg -n ... | head -1` inside a command
# substitution would abort the whole script under `set -euo pipefail` when nothing matches.
first_match_line() {
  local pattern="$1"
  local status=0
  local out=""

  out="$(rg -n "$pattern")" || status=$?
  case "$status" in
    0) printf '%s\n' "$out" | head -1 | cut -d: -f1 ;;
    1) ;;
    *) printf 'ERROR\n' ;;
  esac
}

# Prints the block that starts at the first line matching the pattern and ends at the line
# where the braces opened inside it balance again. Pass - to read standard input.
extract_block() {
  local file="$1"
  local start_pattern="$2"

  # The pattern travels through the environment: awk -v would eat its backslashes.
  EXTRACT_BLOCK_START="$start_pattern" awk '
    BEGIN { start = ENVIRON["EXTRACT_BLOCK_START"] }
    !started && $0 ~ start { started = 1 }
    !started { next }
    {
      line = $0
      opens = gsub(/{/, "{")
      closes = gsub(/}/, "}")
      depth += opens - closes
      if (!opened && opens > 0) opened = 1
      print line
      if (opened && depth <= 0) exit
    }
  ' "$file"
}

# The file with its // and /* */ comments removed and the contents of its string, verbatim
# string and char literals blanked, every line kept so that line numbers still point into the
# real file. Prose must neither trip nor satisfy a check, text inside a message must not read
# as code, and a brace or parenthesis in either must not unbalance extract_block or the
# CodeMatcher tracking.
strip_code() {
  awk '
    BEGIN { sq = sprintf("%c", 39); dq = "\"" }
    {
      line = $0
      out = ""
      n = length(line)
      i = 1
      while (i <= n) {
        c = substr(line, i, 1)
        two = substr(line, i, 2)
        if (state == "block") {
          if (two == "*/") { state = ""; i += 2 } else { i++ }
          continue
        }
        if (state == "verbatim") {
          if (two == dq dq) { i += 2; continue }
          if (c == dq) { out = out dq; state = "" }
          i++
          continue
        }
        if (state == "string" || state == "char") {
          if (c == "\\") { i += 2; continue }
          if ((state == "string" && c == dq) || (state == "char" && c == sq)) { out = out c; state = "" }
          i++
          continue
        }
        if (two == "//") break
        if (two == "/*") { state = "block"; i += 2; continue }
        if (two == "@" dq) { out = out dq; state = "verbatim"; i += 2; continue }
        if (substr(line, i, 3) == "$@" dq || substr(line, i, 3) == "@$" dq) { out = out dq; state = "verbatim"; i += 3; continue }
        if (c == dq) { out = out dq; state = "string"; i++; continue }
        if (c == sq) { out = out sq; state = "char"; i++; continue }
        out = out c
        i++
      }
      if (state == "string" || state == "char") state = ""
      print out
    }
  ' "$1"
}

# Prints "LINE<TAB>TEXT" for every match of the ERE in the text on stdin, searched as one stream
# in which every run of whitespace, line breaks included, is a single space. LINE is the line
# of the text where the match starts. Prints ERROR when the search could not run.
find_all() {
  local pattern="$1"
  local status=0
  local out=""

  out="$(FIND_PATTERN="$pattern" awk '
    {
      line = $0
      gsub(/[ \t\r]+/, " ", line)
      start[NR] = length(stream) + 1
      stream = stream line " "
    }
    END {
      pattern = ENVIRON["FIND_PATTERN"]
      offset = 0
      rest = stream
      while (rest != "" && match(rest, pattern)) {
        at = offset + RSTART
        text = substr(rest, RSTART, RLENGTH)
        if (text ~ /^[^A-Za-z0-9_.]/) { text = substr(text, 2); at++ }
        found = 1
        for (k = 1; k <= NR; k++) {
          if (start[k] > at) break
          found = k
        }
        gsub(/ +/, " ", text)
        printf "%d\t%s\n", found, text
        step = RSTART + RLENGTH - 1
        if (step < 1) step = 1
        offset += step
        rest = substr(rest, step + 1)
      }
    }
  ')" || status=$?

  if ((status != 0)); then
    printf 'ERROR\n'
  elif [[ -n "$out" ]]; then
    printf '%s\n' "$out"
  fi
}

# Exit status 0 when the ERE matches the text on stdin (as find_all sees it), 1 when it does
# not, 2 when the search could not run.
has_match() {
  local hits

  hits="$(find_all "$1")"
  if [[ "$hits" == "ERROR" ]]; then
    return 2
  elif [[ -n "$hits" ]]; then
    return 0
  fi
  return 1
}

# Fails once per match of the ERE in <source>, as "<prefix> (file:line: text)<suffix>". <source>
# starts at line <first_line> of the file. It is an argument rather than stdin on purpose: a
# function at the end of a pipeline runs in a subshell, and the failures it counted would be
# lost with it.
report_matches() {
  local file="$1"
  local first_line="$2"
  local pattern="$3"
  local prefix="$4"
  local suffix="$5"
  local source="$6"
  local hits
  local line
  local text

  hits="$(printf '%s\n' "$source" | find_all "$pattern")"
  if [[ "$hits" == "ERROR" ]]; then
    fail "could not search $file; the check behind this was not run: $prefix"
    return
  fi
  if [[ -z "$hits" ]]; then
    return
  fi
  while IFS=$'\t' read -r line text; do
    fail "$prefix ($file:$((first_line + line - 1)): $text)$suffix"
  done <<<"$hits"
}

# A method declaration by name. A call such as `x = TranspilerLocals.LoadLocal(1)` does not
# match: there is no `static` before the name without an `=` or `(` in between. The character
# class stands in for a word boundary because awk (extract_block) has no \b; there it means a
# backspace, and the pattern would silently never match.
declaration_pattern() {
  printf 'static [^=(]*[^A-Za-z0-9_]%s[ \t]*\\(' "$1"
}

# A rejection against a file that is not there passes every time, so the files are checked
# before anything else and a missing one ends the run.
for source_file in "${match_checked_files[@]}"; do
  if [[ ! -r "$source_file" ]]; then
    fail "cannot read $source_file; none of the checks below would mean anything"
  fi
done

if ((failures > 0)); then
  exit 1
fi

# No transpiler spells a local slot. Slot numbers shift whenever the game adds or removes a
# local, and Harmony does not report a stale one: it emits IL that reads the wrong local. Every
# local is resolved from the method (by type, or from an anchor that is checked against the
# declared local types) and encoded by the exempted helpers.
require_no_hardcoded_local_slots() {
  local locals_file="${1:-$locals_file}"
  local patches_file="${2:-$patches_file}"
  local -a selected_exempt=()
  if (($# == 0)); then selected_exempt=("${exempt_helpers[@]}"); fi
  local stripped_locals
  local stripped_patches
  local name
  local pattern
  local count
  local start
  local block
  local brace
  local length
  local remainder
  local ranges=""

  stripped_locals="$(strip_code "$locals_file")"
  stripped_patches="$(strip_code "$patches_file")"

  for name in "${selected_exempt[@]}"; do
    pattern="$(declaration_pattern "$name")"

    count="$(printf '%s\n' "$stripped_locals" | count_stdin_matches "$pattern")"
    if [[ "$count" != "1" ]]; then
      fail "the exempted opcode helper $name must be declared exactly once in $locals_file (found $count); an exemption that names nothing, or several things, no longer says which code may spell a local slot"
      continue
    fi

    count="$(printf '%s\n' "$stripped_patches" | count_stdin_matches "$pattern")"
    if [[ "$count" != "0" ]]; then
      fail "$patches_file declares $name (found $count); only the helper in $locals_file is exempt, and a second declaration would claim its exemption"
    fi

    start="$(printf '%s\n' "$stripped_locals" | first_match_line "$pattern")"
    block="$(printf '%s\n' "$stripped_locals" | extract_block - "$pattern")"
    brace="$(printf '%s\n' "$block" | first_match_line '\{')"
    if lookup_failed "$start" "$brace" || [[ -z "$start" || -z "$brace" ]] || ((brace > 2)); then
      fail "the exempted opcode helper $name must have a block body that opens on or right after its declaration; otherwise its exemption would run on into whatever follows it"
      continue
    fi
    length="$(printf '%s\n' "$block" | wc -l)"
    ranges+="$start:$((start + length - 1)) "
  done

  # The exempted bodies are blanked rather than removed, so that line numbers stay true.
  remainder="$(printf '%s\n' "$stripped_locals" | EXEMPT_RANGES="$ranges" awk '
    BEGIN {
      n = split(ENVIRON["EXEMPT_RANGES"], r, " ")
      for (k = 1; k <= n; k++) { split(r[k], p, ":"); lo[k] = p[1] + 0; hi[k] = p[2] + 0 }
    }
    {
      for (k = 1; k <= n; k++) if (NR >= lo[k] && NR <= hi[k]) { print ""; next }
      print
    }
  ')"

  local slot_suffix="; resolve the local from the method (by type, or from a checked anchor) and encode it with TranspilerLocals.LoadLocal / StoreLocal"
  local import_suffix="; with it a local slot can be written as a bare Ldloc_1, or through the alias, which the hardcoded-slot check does not see - write OpCodes.<name> instead"

  report_matches "$locals_file" 1 "$hardcoded_slot" \
    "a local slot is hardcoded outside the exempted opcode helpers" "$slot_suffix" "$remainder"
  report_matches "$patches_file" 1 "$hardcoded_slot" \
    "a local slot is hardcoded outside the exempted opcode helpers" "$slot_suffix" "$stripped_patches"
  report_matches "$locals_file" 1 "$opcodes_import" \
    "OpCodes is imported under another name" "$import_suffix" "$remainder"
  report_matches "$patches_file" 1 "$opcodes_import" \
    "OpCodes is imported under another name" "$import_suffix" "$stripped_patches"
}

# R3.6: a CodeMatcher match or search that fails leaves that matcher invalid, and editing,
# advancing or reading the instructions out of it from there patches the wrong place, or
# nothing, with no error. Every match must be followed by ThrowIfInvalid on the same matcher
# before that matcher is used again. The source is read as tokens, so a line break or a space
# before a parenthesis does not hide a call, and each call is attributed to the receiver its
# chain starts from: a variable, or a `new CodeMatcher(...)` (named after the variable it is
# assigned to, if any). A ThrowIfInvalid on one matcher therefore does not excuse another.
require_every_match_is_checked() {
  local file
  local report
  local line
  local matched_files=""

  for file in "${match_checked_files[@]}"; do
    report="$(strip_code "$file" | CHECKED_FILE="$file" awk '
      {
        line = $0
        n = length(line)
        token = ""
        for (i = 1; i <= n; i++) {
          c = substr(line, i, 1)
          if (c ~ /[A-Za-z0-9_]/) {
            if (token == "") token_line = NR
            token = token c
            continue
          }
          if (token != "") { count++; T[count] = token; L[count] = token_line; token = "" }
          if (c == " " || c == "\t" || c == "\r") continue
          count++; T[count] = c; L[count] = NR
        }
        if (token != "") { count++; T[count] = token; L[count] = token_line }
      }
      function report(line, message) {
        printf "%s:%d: %s\n", ENVIRON["CHECKED_FILE"], line, message
      }
      function call(name, receiver, line) {
        if (receiver == "") receiver = "<an unnamed receiver>"
        if (name ~ /^(Match(Start|End)(Forward|Backwards)|MatchForward|MatchBack|SearchForward|SearchBack|SearchBackwards)$/) {
          matches++
          if (pending[receiver])
            report(line, name " follows the match at line " pending[receiver] " on " receiver " before ThrowIfInvalid")
          pending[receiver] = line
        } else if (name == "ThrowIfInvalid") {
          pending[receiver] = 0
        } else if (name ~ /^(Insert|InsertAndAdvance|Advance|InstructionEnumeration|Instructions|Set|SetInstruction|SetInstructionAndAdvance|SetOpcodeAndAdvance|SetOperandAndAdvance|SetAndAdvance|RemoveInstruction|RemoveInstructions|RemoveInstructionsInRange|RemoveInstructionsWithOffsets)$/) {
          if (pending[receiver]) {
            report(line, name " follows the match at line " pending[receiver] " on " receiver " before ThrowIfInvalid")
            pending[receiver] = 0
          }
        }
      }
      END {
        depth = 0
        unnamed = 0
        for (k = 1; k <= count; k++) {
          t = T[k]
          if (t == "(") { depth++; root[depth] = ""; assigned[depth] = ""; type_follows[depth] = 0; continue }
          if (t == ")") { if (depth > 0) depth--; continue }
          if (t == ";" || t == "," || t == "{" || t == "}") { root[depth] = ""; assigned[depth] = ""; type_follows[depth] = 0; continue }
          if (t == "=") {
            if (T[k + 1] != "=" && T[k + 1] != ">" && T[k - 1] !~ /^[=!<>]$/ && T[k - 1] ~ /^[A-Za-z_]/)
              assigned[depth] = T[k - 1]
            continue
          }
          if (t !~ /^[A-Za-z_]/) continue
          if (T[k - 1] == ".") {
            if (T[k + 1] == "(") call(t, root[depth], L[k])
            continue
          }
          if (t == "new") {
            unnamed++
            root[depth] = assigned[depth] != "" ? assigned[depth] : "new CodeMatcher #" unnamed
            type_follows[depth] = 1
            continue
          }
          if (type_follows[depth]) { type_follows[depth] = 0; continue }
          root[depth] = t
        }
        for (receiver in pending)
          if (pending[receiver])
            report(pending[receiver], "the match at line " pending[receiver] " on " receiver " is never followed by ThrowIfInvalid")
        printf "matches=%d\n", matches
      }
    ')"

    while IFS= read -r line; do
      case "$line" in
        matches=*)
          if [[ "${line#matches=}" != "0" ]]; then
            matched_files+="$file"$'\n'
          fi
          ;;
        "") ;;
        *) fail "a CodeMatcher match is used without ThrowIfInvalid ($line); a failed match would then patch the wrong place, or nothing, without an error" ;;
      esac
    done <<<"$report"
  done

  for file in "${matching_files[@]}"; do
    case $'\n'"$matched_files" in
      *$'\n'"$file"$'\n'*) ;;
      *) fail "no CodeMatcher match was found in $file; the ThrowIfInvalid check had nothing to look at, so it did not show anything" ;;
    esac
  done
}

# A resolver that returns a fallback instead of refusing is how a transpiler reads the wrong
# local without anyone noticing: an unknown operand form, an anchor that matches zero or several
# times, or a local that is undeclared or of another type must all end the patch.
require_the_resolvers_refuse() {
  local stripped
  local name
  local block
  local status

  stripped="$(strip_code "$locals_file")"
  for name in "${refusing_resolvers[@]}"; do
    block="$(printf '%s\n' "$stripped" | extract_block - "$(declaration_pattern "$name")")"
    if [[ -z "$block" ]]; then
      fail "TranspilerLocals.$name was not found in $locals_file; the check that it refuses what it cannot resolve was not run"
      continue
    fi

    status=0
    printf '%s\n' "$block" | tail -n +2 | has_match '(^|[^A-Za-z0-9_])throw([^A-Za-z0-9_]|$)' || status=$?
    case "$status" in
      0) ;;
      1) fail "TranspilerLocals.$name must throw when it cannot resolve its input (an unknown operand form, an anchor that does not match exactly once, a local that is undeclared or of the wrong type); a fallback value would let a transpiler read the wrong local" ;;
      *) fail "could not search TranspilerLocals.$name; the check that it refuses what it cannot resolve was not run" ;;
    esac
  done
}

require_in_block() {
  local name="$1"
  local block="$2"
  local pattern="$3"
  local message="$4"
  local status=0

  printf '%s\n' "$block" | has_match "$pattern" || status=$?
  case "$status" in
    0) ;;
    1) fail "$name $message" ;;
    *) fail "could not search $name; the check behind this was not run: $name $message" ;;
  esac
}

# One path from the game's IL to the injected calls: each anchored transpiler hands its
# instructions to its derivation function and places what it emits with InsertBackToFront, and
# derives nothing on its own; the derivation function locates its anchor with FindSingle, checks
# each local with RequireLocal, and reads the declared local types from the method itself. Those
# are the functions tests/TranspilerLocalsHarness executes, so a second path inside a transpiler
# would be exactly the code nothing offline has run.
require_one_derivation_path_per_transpiler() {
  local stripped_patches
  local stripped_locals
  local pair
  local name
  local derivation
  local declaration
  local start
  local block

  stripped_patches="$(strip_code "$patches_file")"
  stripped_locals="$(strip_code "$locals_file")"

  for pair in "${anchored_transpilers[@]}"; do
    name="${pair%%:*}"
    derivation="${pair#*:}"

    declaration="(^|[^A-Za-z0-9_.])${name}[ \t]*\\("
    start="$(printf '%s\n' "$stripped_patches" | first_match_line "$declaration")"
    block="$(printf '%s\n' "$stripped_patches" | extract_block - "$declaration")"
    if lookup_failed "$start" || [[ -z "$start" || -z "$block" ]]; then
      fail "$name was not found in $patches_file; the checks on how it derives its locals were not run"
    else
      require_in_block "$name" "$block" "TranspilerLocals *\\. *${derivation} *\\(" \
        "must derive its locals and injection points with TranspilerLocals.$derivation, the code tests/TranspilerLocalsHarness executes"
      require_in_block "$name" "$block" 'TranspilerLocals *\. *InsertBackToFront *\(' \
        "must place its injected calls with TranspilerLocals.InsertBackToFront, which applies them from the back so that no position shifts under another"
      report_matches "$patches_file" "$start" "$own_resolution" \
        "$name derives or places instructions itself" \
        "; only TranspilerLocals.$derivation and InsertBackToFront may, so that the harness executes the one path the game runs" \
        "$block"
    fi

    block="$(printf '%s\n' "$stripped_locals" | extract_block - "$(declaration_pattern "$derivation")")"
    if [[ -z "$block" ]]; then
      fail "TranspilerLocals.$derivation was not found in $locals_file; the checks on how it derives the locals of $name were not run"
      continue
    fi
    require_in_block "TranspilerLocals.$derivation" "$block" '(^|[^A-Za-z0-9_])FindSingle *\(' \
      "must locate its anchor with FindSingle, which refuses an anchor that does not match exactly once in the method"
    require_in_block "TranspilerLocals.$derivation" "$block" '(^|[^A-Za-z0-9_])RequireLocal *\(' \
      "must check each derived local with RequireLocal against the method's declared local types"
    require_in_block "TranspilerLocals.$derivation" "$block" 'original *\?? *\. *GetMethodBody *\( *\)' \
      "must read the declared local types from original.GetMethodBody() rather than from an assumed layout"
  done

  # queuedOre is also the only string local of UpdateSmelter; the derivation checks the anchor
  # against that, so an anchor on a different store than the one the game reads is refused.
  block="$(printf '%s\n' "$stripped_locals" | extract_block - "$(declaration_pattern DeriveSmelterLoop)")"
  if [[ -n "$block" ]]; then
    require_in_block "TranspilerLocals.DeriveSmelterLoop" "$block" '(^|[^A-Za-z0-9_])SingleLocalOfType *\(' \
      "must check queuedOre against the method's only string local with SingleLocalOfType"
  fi
}

require_no_hardcoded_local_slots
require_no_hardcoded_local_slots "$repair_locals_file" "$repair_patches_file"
require_every_match_is_checked
require_the_resolvers_refuse
require_one_derivation_path_per_transpiler

# Keep the complete Repair transformation on one path through the shipped validators. A mere
# call-presence check would accept a discarded local result or a bypass of Show uniqueness.
repair_body="$(strip_code "$repair_patches_file")"
repair_body="$(printf '%s\n' "$repair_body" | extract_block - 'public static IEnumerable<CodeInstruction> CraftingStation_Interact_Transpiler\(' | sed ':join;N;$!b join;s/[[:space:]]//g')"
repair_expected='publicstaticIEnumerable<CodeInstruction>CraftingStation_Interact_Transpiler(IEnumerable<CodeInstruction>instructions,MethodBaseoriginal){varcode=instructions.ToList();varplayerLocal=TranspilerLocals.RequireUniqueLocal(original,typeof(Player));varshow=AccessTools.Method(typeof(InventoryGui),nameof(InventoryGui.Show));TranspilerLocals.RequireUniqueShow(code.Select(instruction=>instruction.opcode==OpCodes.Callvirt&&Equals(instruction.operand,show)));returnnewCodeMatcher(code).MatchStartForward(newCodeMatch(OpCodes.Callvirt,show)).ThrowIfInvalid("").Advance(1).Insert(newCodeInstruction(OpCodes.Ldloc,playerLocal),newCodeInstruction(OpCodes.Ldarg_0),newCodeInstruction(OpCodes.Call,AccessTools.Method(typeof(ItemRepair),nameof(ItemRepair.CraftingStationInteractHook)))).InstructionEnumeration();}'
if [[ "$repair_body" != "$repair_expected" ]]; then
  fail 'Repair transpiler must use the shared Player and unique Show validators, check its match, and inject only the resolved Player immediately after Show'
fi

if ((failures > 0)); then
  exit 1
fi

printf 'PASS transpiler static invariant checks\n'
