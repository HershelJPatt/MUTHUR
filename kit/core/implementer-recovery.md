# Implementer — assignment recovery

The full assignment check, for a worktree the launcher did not verify: a harness's own worktree isolation
cuts the tree from the repository's HEAD, not from the branch the orchestrator named, so arriving on the
wrong lineage is the ordinary case rather than the rare one. In a single day, thirteen units across two
orchestrators came up on the default branch: no spec, and none of the units already integrated. Not one
was built on the wrong lineage, and the only reason is that every one of those implementers ran this check
before writing anything. When the assignment block says the launcher verified it, the short check in the
contract is enough and this file is not read. Everywhere else this check is not belt-and-braces, and it is
not yours to tidy away.

Require all three dispatch inputs: the named local base branch, its full commit SHA at dispatch, and the named default branch.
Missing input means `STATUS: blocked`. The base must be a **branch**, not a commit: `git rev-parse <sha>`
never moves, so identity would hold forever and an amendment to the spec would be invisible. The dispatch
SHA complements the live branch; it does not replace it.

A repository can hold a main checkout and many sibling worktrees sharing one `.git`, so never read a file
through a path that leads into another checkout — every check below would pass while you read another
branch's copy.

Before any ancestry test, reset or spec read, require each of these commands to succeed:
`git show-ref --verify "refs/heads/<base>"`,
`git rev-parse --verify "refs/heads/<base>^{commit}"`, and
`git rev-parse --verify "HEAD^{commit}"`. Record initial HEAD and resolved base SHA.
Substitute the supplied names and paths for placeholders such as `<base>`, `<default branch>` and
`<spec path>`; the quotes in these examples protect arguments containing braces, colons or spaces.
Require resolved base SHA to equal the full dispatch SHA. Missing refs, failed resolution or a moved
base mean `STATUS: blocked`: report expected branch/SHA and observed HEAD/base (or missing) in BASE/NOTES.
Make no product edits, reset, merge or rebase, and never fall back to another checkout. The orchestrator
must redispatch with an updated frozen assignment if the base moved.

Require `git status --porcelain` to succeed and be empty before reconciliation; otherwise blocked.
Require `git cat-file -t "refs/heads/<base>:<spec path>"` to succeed and return `blob`; a missing
committed spec, another object type or any failed Git command blocks. These checks also apply when
initial HEAD matches the base. Compare initial HEAD and resolved base SHA to confirm **they are the same
commit**, or reconcile below. Presence of the spec file is not enough: a stale ancestor often already
contains an older version of it, so the file is there, the check passes, and you build from a spec that
has since been amended. That failure looks correct all the way to the verdict. If the two commits differ,
ask what this branch carries that exists nowhere else — not who wrote it, which git cannot tell you.
For `git merge-base --is-ancestor`, exit 0 means ancestor, 1 means not ancestor, and any other exit code
means `STATUS: blocked`. Never treat a Git error as permission to reset.

- **If `git merge-base --is-ancestor HEAD "refs/heads/<base>"` exits 0**, everything here is already in the
  base, so the reset only moves this branch forward and discards no commit at all: with `git status --porcelain`
  empty, `git reset --hard "refs/heads/<base>"`, and report it as a fast-forward reset. If it is not empty, stop
  and report `blocked` — you did not dirty this worktree, and what those uncommitted changes are worth is not
  yours to decide. This covers a fresh worktree cut from an ancestor, and equally one whose own commits the
  orchestrator has already integrated — the ordinary reason for the base to move under you, and it leaves you
  nothing to lose even when it moved files in your unit.
- **Otherwise, first resolve the named default branch** with
  `git show-ref --verify "refs/heads/<default branch>"` and
  `git rev-parse --verify "refs/heads/<default branch>^{commit}"`; both must succeed or you are blocked.
  **If `git merge-base --is-ancestor HEAD "refs/heads/<default branch>"` exits 0**, everything here is
  already landed. The extra commits are inherited from another lineage and belong to nobody in this unit.
  Say it plainly, because `git reset --hard` is the only destructive command in this contract and you are
  about to run it: **the commits it drops off your branch are not yours.** Every one of them is already on
  the default branch — that is exactly what the check you just ran proved — so each belongs to a task that
  has already landed, and none of your unit's work can be among them, because you have not written any yet
  and unlanded work could not have passed that check. Reset the same way and on the same clean-tree
  condition, and report it as a **divergent** reset. Run `git log --oneline "refs/heads/<base>..HEAD"` so your
  report can say what the reset discarded, but never let that list decide: it measures history, not
  containment, and commits you never wrote sit in it looking exactly like your own.
- **Otherwise** this branch holds commits that are in neither the base nor the default branch — work that
  exists only here, so you are being resumed. Do not reset, do not merge and do not rebase. If you did not
  write those commits yourself, stop and report `blocked`: unlanded work you cannot account for is the
  orchestrator's to place, not yours. Re-read the spec from the base as below, because the orchestrator
  has very likely amended it and your copy is the old one. Your commits sit on the old base, so run
  `git diff --stat <your starting commit>..<base>` and say in your report whether it moved any file in
  your unit; if it did, stop and report `blocked` rather than guessing — you would be editing a stale
  copy, and your branch would clobber the newer one at merge. Otherwise commit your new work on top of
  what you have, and say in your report that you did this. If the two histories have genuinely diverged
  in a way you cannot read past, stop and report `blocked` too: recovering a mixed history is the
  orchestrator's decision, not yours.

After either allowed reset, require reset command success, resulting HEAD equal to the resolved base SHA,
base still equal to dispatch SHA, and `git status --porcelain` successful and empty; otherwise blocked.
Verify HEAD with `git rev-parse --verify "HEAD^{commit}"` and base with
`git rev-parse --verify "refs/heads/<base>^{commit}"`. Resumed work is not required to have HEAD equal
base; it retains the ownership and changed-unit checks above and must still verify the base has not moved.
After reconciliation and immediately before reading the spec, re-resolve the named base with
`git rev-parse --verify "refs/heads/<base>^{commit}"` and require it still matches dispatch SHA.
Then read the spec from the base, always — `git show "refs/heads/<base>:<spec path>"` must succeed — not
from your working tree and not only when you are resumed. Matching commits say nothing about the files on
disk: a stray revert, a partially applied stash, or a harness that writes files rather than checking them
out all leave HEAD exactly where it belongs and the spec stale. Reading through `<base>` makes the working
tree irrelevant to what you read, which is the only way to be sure the bytes are the ones your orchestrator
froze. And `git status --porcelain` must be empty before you begin: `git show` secures a read; the work
itself happens in the working tree, so a clean tree is the only thing that secures an edit.

Never begin work against a tree whose spec you could not verify. A spec read from the wrong base is the
wrong spec, and the work will look correct and be wrong. Never recover the spec alone with `git cat-file`
or `git show` and continue building an unreconciled wrong lineage. Any failed verification above means
`STATUS: blocked`, with no product edits.

Whatever this procedure did, say so on the report's BASE line, in the contract's format:
`BASE: <named base branch, dispatch SHA, initial HEAD, resulting HEAD, and any recovery>`. If blocked, NOTES carries the
expected branch/SHA and observed HEAD/base (or missing), including missing refs/spec or a moved base.
