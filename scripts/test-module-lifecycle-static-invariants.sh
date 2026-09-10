#!/usr/bin/env bash
#
# Source-shape invariants for the module lifecycle framework.
#
# These are regression tripwires, not proofs. Each one is tied to a defect shape that
# review actually found, and none of them interprets C#: "the deferred return is guarded"
# means the guard is written near the return, not that it dominates it. The properties
# that can genuinely be established offline are executed by tests/ModuleLifecycleHarness
# instead; the rest stay open until the game can be run.
#
# What a tripwire may never do is fail to run and call that a pass. Every search below
# distinguishes "no match" from "could not search", and a missing or unreadable file stops
# the script rather than quietly satisfying every reject_source_pattern in it.
#
# Framework side only. The pilot-module checks (I1-I7, I10, I11) arrive with the module
# they check.
#
set -euo pipefail

failures=0
registry_file="Automatics/ModuleRegistry.cs"
scope_file="Automatics/ModuleScope.cs"
lifecycle_file="Automatics/ModuleLifecycle.cs"
machine_file="Automatics/ModuleTransitionMachine.cs"
plugin_file="Automatics/Automatics.cs"
root_patches_file="Automatics/Patches.cs"

# Identifiers that are followed by an argument list but are not calls.
keywords="if|for|foreach|while|switch|catch|using|lock|return|new|do|fixed|sizeof|typeof|nameof"

fail() {
  printf 'FAIL: %s\n' "$*" >&2
  failures=$((failures + 1))
}

# rg exits 0 on a match, 1 on no match, and 2 or more on an error. Collapsing the last two
# is how a check silently stops checking, so each helper below spells the three out.
require_source_pattern() {
  local file="$1"
  local pattern="$2"
  local message="$3"
  local status=0

  rg -q "$pattern" "$file" || status=$?
  case "$status" in
    0) ;;
    1) fail "$message" ;;
    *) fail "could not search $file (rg exit $status); the check behind this was not run: $message" ;;
  esac
}

reject_source_pattern() {
  local file="$1"
  local pattern="$2"
  local message="$3"
  local status=0

  rg -q "$pattern" "$file" || status=$?
  case "$status" in
    0) fail "$message" ;;
    1) ;;
    *) fail "could not search $file (rg exit $status); the check behind this was not run: $message" ;;
  esac
}

# Prints the number of matching lines, or the word ERROR, which no caller accepts.
count_matches() {
  local file="$1"
  local pattern="$2"
  local status=0
  local count=""

  count="$(rg -c "$pattern" "$file")" || status=$?
  case "$status" in
    0) printf '%s\n' "$count" ;;
    1) printf '0\n' ;;
    *) printf 'ERROR\n' ;;
  esac
}

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

# Prints the 1-based line number of the first match in the text on stdin, nothing when
# there is no match, and ERROR when the search could not run. Written this way because
# `rg -n ... | head -1` inside a command substitution aborts the whole script under
# `set -euo pipefail` when there is no match: the check would then neither report nor let
# the checks after it run.
# True when any argument is the ERROR marker from first_match_line. ERROR is not a number,
# and bash arithmetic would read it as 0, so a search that could not run has to be caught
# before any comparison rather than after it.
lookup_failed() {
  local value

  for value in "$@"; do
    if [[ "$value" == "ERROR" ]]; then
      return 0
    fi
  done

  return 1
}

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

# Prints the block that starts at the first line matching the pattern and ends at the
# line where the braces opened inside it balance again. Pass - to read standard input.
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

# Every identifier that is applied to an argument list, comments stripped. A search that
# could not run emits a token no allowlist matches, so the caller fails instead of seeing
# an empty and therefore innocent-looking result.
calls_in() {
  local status=0

  sed -e 's://.*::' | rg -o '[A-Za-z_][A-Za-z0-9_]*\s*\(' || status=$?
  if ((status > 1)); then
    printf '<<the search for calls could not be run>>\n'
  fi
}

# A reject_source_pattern against a file that is not there passes every time, so the files
# are checked before anything else and a missing one ends the run.
for source_file in \
  "$registry_file" "$scope_file" "$lifecycle_file" \
  "$machine_file" "$plugin_file" "$root_patches_file"; do
  if [[ ! -r "$source_file" ]]; then
    fail "cannot read $source_file; none of the checks below would mean anything"
  fi
done

if ((failures > 0)); then
  exit 1
fi

# I8 (R1.3b): the registry reads the toggle and never writes it. The UI and the applied
# state are reconciled by reporting the state, not by rewriting the user's setting.
reject_source_pattern \
  "$registry_file" \
  "\\.Value\\s*=[^=]" \
  "the module registry must never assign a config value (R1.3b: the toggle is read only)"

# I8 (R1.4): a setting-changed handler is raised from the ConfigurationManager OnGUI pass
# and from a config file reload, so it may only record intent - and it is a game call
# stack, so it must also contain whatever it provokes.
require_setting_changed_handler_only_requests() {
  local body
  local offending

  body="$(extract_block "$registry_file" "SettingChanged \\+=" | tail -n +2)"
  if [[ -z "$body" ]]; then
    fail "the module registry must subscribe to the module toggle's SettingChanged event"
    return
  fi

  if ! printf '%s\n' "$body" | rg -q "RequestTransition\\("; then
    fail "the SettingChanged handler must hand the change to RequestTransition"
    return
  fi

  if ! printf '%s\n' "$body" | rg -q "catch"; then
    fail "the SettingChanged handler must contain its own failures; it runs on a game call stack"
  fi

  offending="$(
    printf '%s\n' "$body" | calls_in |
      rg -v "^($keywords|RequestTransition|Contain)\\s*\\(" || true
  )"
  if [[ -n "$offending" ]]; then
    fail "the SettingChanged handler must request the transition and contain it, nothing else (R1.4), found: ${offending//$'\n'/ }"
  fi
}

# The pump is called from UnityPlugin.Update. The transition callbacks are contained by the
# machine, but nothing else in the loop is, and an escape here reaches the update loop.
require_pump_contains_its_failures() {
  local body

  body="$(extract_block "$registry_file" "public static void Pump\\(\\)")"
  if [[ -z "$body" ]]; then
    fail "the module registry must define Pump()"
    return
  fi

  if ! printf '%s\n' "$body" | rg -q "catch"; then
    fail "Pump must contain its own failures; it runs from the update loop"
  fi
}

# I15 (R1.5a / R1.4): raising the stop request is the one structural thing that happens at
# request time, because the observable stop boundary has to be the frame the user asked
# for. Contain is allowed alongside it: containment is not structural work.
require_request_transition_shape() {
  local body
  local offending

  body="$(extract_block "$registry_file" "private static void RequestTransition" | tail -n +2)"
  if [[ -z "$body" ]]; then
    fail "the module registry must define RequestTransition"
    return
  fi

  if ! printf '%s\n' "$body" | rg -q "SetStopRequested\\(!enable\\)"; then
    fail "RequestTransition must raise and lower the stop request as SetStopRequested(!enable) (R1.5a)"
  fi

  if ! printf '%s\n' "$body" | rg -q "\\.Request\\(enable\\)"; then
    fail "RequestTransition must leave the structural transition to the pump via Request(enable)"
  fi

  offending="$(
    printf '%s\n' "$body" | calls_in |
      rg -v "^($keywords|SetStopRequested|Request|Contain)\\s*\\(" || true
  )"
  if [[ -n "$offending" ]]; then
    fail "RequestTransition must do nothing but the stop request, the transition request and containment (R1.4), found: ${offending//$'\n'/ }"
  fi
}

# I9 (R1.6 / R3.1): removing patches is the scope's business alone, and the always-on root
# patches have no removal path at all.
require_unpatch_calls_only_in_the_scope() {
  local files
  local status=0

  # The call syntax, not the name: the capability probe resolves the same API by
  # reflection, so the string "UnpatchSelf" legitimately appears in $lifecycle_file and
  # that file is excluded here rather than the check being weakened for everyone.
  files="$(
    rg -l --glob "!Automatics/Libraries/**" --glob "!$lifecycle_file" \
      "\\.UnpatchSelf\\(" Automatics
  )" || status=$?

  if ((status > 1)); then
    fail "could not search for UnpatchSelf call sites (rg exit $status); the check was not run"
    return
  fi

  if [[ "$files" != "$scope_file" ]]; then
    fail "UnpatchSelf() must be called from $scope_file and nowhere else (R3.1), found: ${files//$'\n'/ }"
  fi
}

reject_source_pattern \
  "$root_patches_file" \
  "Unpatch[A-Za-z]*\\s*\\(" \
  "the always-on root patches must have no removal path (R3.1)"
reject_source_pattern \
  "$plugin_file" \
  "Unpatch[A-Za-z]*\\s*\\(" \
  "the plugin entry point must not remove the always-on root patches (R3.1)"

# I9 (AC-S10): the transition layer stays offline-executable. Nothing that needs the engine,
# the patch library or the config framework may leak into it.
reject_source_pattern \
  "$machine_file" \
  "UnityEngine|HarmonyLib|BepInEx|ConfigEntry" \
  "the transition layer must not depend on the engine, Harmony, BepInEx or config entries (AC-S10)"

# I12 (MAJOR-1): Deferred asserts that this attempt left nothing behind. A rollback that
# failed must be reported as a failure, or the machine calls a module disabled while its
# patches and subscriptions are still live.
require_source_pattern \
  "$registry_file" \
  "return TransitionOutcome\\.Deferred" \
  "the module registry must be able to defer an activation (R4.5)"

require_deferred_returns_are_guarded() {
  local unguarded

  unguarded="$(
    awk '
      { lines[NR] = $0 }
      /return TransitionOutcome\.Deferred/ {
        start = NR - 4
        if (start < 1) start = 1
        guarded = 0
        for (i = start; i <= NR; i++)
          if (lines[i] ~ /TeardownIsClean/) guarded = 1
        if (!guarded) print NR
      }
    ' "$registry_file"
  )"

  if [[ -n "$unguarded" ]]; then
    fail "a deferred activation must be returned only after TeardownIsClean confirmed the rollback (MAJOR-1), unguarded at line(s): ${unguarded//$'\n'/ }"
  fi
}

# I13 (MAJOR-2): a scope whose teardown did not complete is the only handle on what is
# still installed. Both the failed-activation paths and the failed-teardown path keep it.
require_failed_scopes_are_kept() {
  local kept

  kept="$(count_matches "$registry_file" "Residual\\s*=\\s*scope")"
  if [[ "$kept" == "ERROR" ]]; then
    fail "could not count the scopes kept for recovery; the check was not run"
    return
  fi

  if ((kept < 3)); then
    fail "every failure path must keep its scope for recovery (MAJOR-2): expected at least 3 assignments of Residual, found $kept"
  fi
}

# I13 (MAJOR-2): and it is only let go where the teardown really finished.
require_residual_is_cleared_only_after_a_clean_teardown() {
  local total
  local in_recover
  local in_disable

  total="$(count_matches "$registry_file" "Residual\\s*=\\s*null")"
  in_recover="$(
    extract_block "$registry_file" "private static TransitionOutcome RecoverTransition" |
      count_stdin_matches "Residual\\s*=\\s*null"
  )"
  in_disable="$(
    extract_block "$registry_file" "private static TransitionOutcome DisableTransition" |
      count_stdin_matches "Residual\\s*=\\s*null"
  )"

  if [[ "$total" == "ERROR" || "$in_recover" == "ERROR" || "$in_disable" == "ERROR" ]]; then
    fail "could not count where the residual scope is released; the check was not run"
    return
  fi

  if ((in_recover < 1)); then
    fail "recovery must release the residual scope once its teardown completed (MAJOR-2)"
  fi

  if ((in_disable < 1)); then
    fail "a clean teardown must leave no residual scope behind (MAJOR-2)"
  fi

  if ((total != in_recover + in_disable)); then
    fail "the residual scope must be released only by recovery and by a clean teardown (MAJOR-2): $total assignments of null, $in_recover in recovery and $in_disable in the teardown"
  fi
}

# Recovery is a new attempt at the same teardown, so it has to repeat the work whose report
# it drops. Clearing without redoing turns an unfinished cleanup into a clean one.
require_recovery_retries_before_it_clears() {
  local body
  local disable_line
  local clear_line

  body="$(extract_block "$registry_file" "private static TransitionOutcome RecoverTransition")"
  if [[ -z "$body" ]]; then
    fail "the module registry must define RecoverTransition"
    return
  fi

  disable_line="$(printf '%s\n' "$body" | first_match_line "Module\\.Disable\\(")"
  clear_line="$(printf '%s\n' "$body" | first_match_line "ClearRecordedFailures\\(")"

  if lookup_failed "$disable_line" "$clear_line"; then
    fail "could not search for recovery ordering; the check was not run"
    return
  fi

  if [[ -z "$disable_line" || -z "$clear_line" ]]; then
    fail "recovery must redo the module's own teardown and only then drop the previous report"
    return
  fi

  if ((disable_line >= clear_line)); then
    fail "recovery must repeat the module teardown before it clears the report of the attempt that failed"
  fi
}

# I14 (MAJOR-7): the subscription token removes first and only marks itself spent once the
# removal returned. Clearing first makes a failed removal look done and loses the only
# handle on a subscription that is still live.
require_token_removes_before_it_clears() {
  local body
  local invoke_line
  local clear_line

  body="$(
    extract_block "$machine_file" "class SubscriptionToken<T>" |
      extract_block - "public void Dispose\\(\\)"
  )"
  if [[ -z "$body" ]]; then
    fail "SubscriptionToken<T> in $machine_file must define Dispose()"
    return
  fi

  invoke_line="$(printf '%s\n' "$body" | first_match_line "remove\\(")"
  clear_line="$(printf '%s\n' "$body" | first_match_line "_remove = null")"

  if lookup_failed "$invoke_line" "$clear_line"; then
    fail "could not search for the subscription token teardown order; the check was not run"
    return
  fi

  if [[ -z "$invoke_line" || -z "$clear_line" ]]; then
    fail "SubscriptionToken<T>.Dispose must both run the removal and mark the token spent"
    return
  fi

  if ((invoke_line >= clear_line)); then
    fail "SubscriptionToken<T>.Dispose must run the removal before it marks the token spent (MAJOR-7)"
  fi
}

# The scope acquires before it registers in only one place left - the component stamp - and
# everywhere else registers the undo first, so an acquisition that throws part way through
# still has a removal behind it.
require_scope_registers_before_it_applies() {
  local body
  local register_line
  local apply_line

  body="$(extract_block "$scope_file" "public void PatchAll\\(Type patchType\\)")"
  if [[ -z "$body" ]]; then
    fail "the module scope must define PatchAll(Type)"
    return
  fi

  register_line="$(printf '%s\n' "$body" | first_match_line "RegisterOrRelease\\(")"
  apply_line="$(printf '%s\n' "$body" | first_match_line "harmony\\.PatchAll\\(")"

  if lookup_failed "$register_line" "$apply_line"; then
    fail "could not search for the acquisition order; the check was not run"
    return
  fi

  if [[ -z "$register_line" || -z "$apply_line" ]]; then
    fail "PatchAll must both apply the patches and register their removal"
    return
  fi

  if ((register_line >= apply_line)); then
    fail "PatchAll must register the removal before it applies anything: a partial application must not be left without one"
  fi

  body="$(extract_block "$scope_file" "public IDisposable Subscribe<T>")"
  if [[ -z "$body" ]]; then
    fail "the module scope must define Subscribe<T>"
    return
  fi

  register_line="$(printf '%s\n' "$body" | first_match_line "RegisterOrRelease\\(")"
  apply_line="$(printf '%s\n' "$body" | first_match_line "^\\s*add\\(handler\\)")"

  if lookup_failed "$register_line" "$apply_line"; then
    fail "could not search for the acquisition order; the check was not run"
    return
  fi

  if [[ -z "$register_line" || -z "$apply_line" ]]; then
    fail "Subscribe must both add the handler and register its removal"
    return
  fi

  if ((register_line >= apply_line)); then
    fail "Subscribe must register the removal before it adds the handler: an add that subscribes and then throws must not be left without one"
  fi
}

# The pump's containment has to start at the list read, not after it, or the boundary the
# method advertises is not the boundary it has.
require_pump_containment_starts_at_the_top() {
  local body
  local try_line
  local read_line

  body="$(extract_block "$registry_file" "public static void Pump\\(\\)")"
  if [[ -z "$body" ]]; then
    fail "the module registry must define Pump()"
    return
  fi

  try_line="$(printf '%s\n' "$body" | first_match_line "^\\s*try$")"
  read_line="$(printf '%s\n' "$body" | first_match_line "Entries\\[i\\]")"

  if lookup_failed "$try_line" "$read_line"; then
    fail "could not search for Pump's containment boundary; the check was not run"
    return
  fi

  if [[ -z "$try_line" || -z "$read_line" ]]; then
    fail "Pump must read each entry inside its containment"
    return
  fi

  if ((try_line >= read_line)); then
    fail "Pump's containment must start before it reads an entry, not after"
  fi
}

# One scope owns one Harmony id, and removing an id's patches removes all of them. A second
# registration would take the first call's patches off too, and at the wrong point in the
# reverse walk - before the subscriptions and components acquired after them.
require_patch_removal_registered_once() {
  local body
  local registrations
  local guard_line
  local register_line

  body="$(extract_block "$scope_file" "public void PatchAll\\(Type patchType\\)")"
  if [[ -z "$body" ]]; then
    fail "the module scope must define PatchAll(Type)"
    return
  fi

  registrations="$(printf '%s\n' "$body" | count_stdin_matches "RegisterOrRelease\\(")"
  if [[ "$registrations" == "ERROR" ]]; then
    fail "could not count the patch removal registrations; the check was not run"
    return
  fi

  if ((registrations != 1)); then
    fail "PatchAll must register exactly one removal for the whole scope (found $registrations): one Harmony id means one removal, at the position of the first call"
  fi

  guard_line="$(printf '%s\n' "$body" | first_match_line "_harmony == null")"
  register_line="$(printf '%s\n' "$body" | first_match_line "RegisterOrRelease\\(")"

  if lookup_failed "$guard_line" "$register_line"; then
    fail "could not search for the patch removal registration; the check was not run"
    return
  fi

  if [[ -z "$guard_line" || -z "$register_line" ]]; then
    fail "PatchAll must register the scope's single patch removal behind a first-call guard"
    return
  fi

  if ((guard_line >= register_line)); then
    fail "PatchAll must guard the removal registration so that only the first call makes it"
  fi
}

# The refusal path has to give the same isolation the two ledger entries give: a hook that
# throws must not stop the destruction.
require_refusal_release_isolates_the_halves() {
  local body
  local detach_line
  local destroy_line
  local catch_line

  body="$(extract_block "$scope_file" "private void DetachThenDestroy")"
  if [[ -z "$body" ]]; then
    fail "the module scope must define the refusal-time release for a component"
    return
  fi

  detach_line="$(printf '%s\n' "$body" | first_match_line "DetachTracked\\(")"
  destroy_line="$(printf '%s\n' "$body" | first_match_line "DestroyTracked\\(")"
  catch_line="$(printf '%s\n' "$body" | first_match_line "catch")"

  if lookup_failed "$detach_line" "$destroy_line" "$catch_line"; then
    fail "could not search for the refusal-time release; the check was not run"
    return
  fi

  if [[ -z "$detach_line" || -z "$destroy_line" ]]; then
    fail "the refusal-time release must run both the detach hook and the destruction"
    return
  fi

  if ((detach_line >= destroy_line)); then
    fail "the refusal-time release must run the detach hook before the destruction"
  fi

  if [[ -z "$catch_line" ]] || ((catch_line >= destroy_line)); then
    fail "the refusal-time release must isolate a throwing detach hook from the destruction"
  fi
}

# An undo is arbitrary code and may add an obligation, so a list position taken before it
# means nothing afterwards. This is the same hazard that was removed from the component
# bookkeeping; the orphan walk must not reintroduce it.
require_orphan_walk_is_identity_based() {
  local body

  body="$(extract_block "$scope_file" "private void DisposeOrphans")"
  if [[ -z "$body" ]]; then
    fail "the module scope must define the refused-obligation walk"
    return
  fi

  if printf '%s\n' "$body" | rg -q "RemoveAt\\(i\\)"; then
    fail "the refused-obligation walk must not hold a list position across an undo: resolve the completed entry by identity"
  fi

  if ! printf '%s\n' "$body" | rg -q "RemoveOrphan\\("; then
    fail "the refused-obligation walk must remove a completed obligation by identity"
  fi
}

require_setting_changed_handler_only_requests
require_pump_contains_its_failures
require_pump_containment_starts_at_the_top
require_request_transition_shape
require_unpatch_calls_only_in_the_scope
require_deferred_returns_are_guarded
require_failed_scopes_are_kept
require_residual_is_cleared_only_after_a_clean_teardown
require_recovery_retries_before_it_clears
require_token_removes_before_it_clears
require_scope_registers_before_it_applies
require_patch_removal_registered_once
require_refusal_release_isolates_the_halves
require_orphan_walk_is_identity_based

if ((failures > 0)); then
  exit 1
fi

printf 'PASS module lifecycle static invariant checks\n'
