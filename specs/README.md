# specs/

A frozen spec per task: what was asked, precisely enough that an implementer could build it without making
a design decision. `_TEMPLATE.md` is the shape.

## Naming

`T-<id>-<slug>.md`. The id is the ledger task the spec was written for; the slug comes from the spec's own
first heading.

**Ids in here are not unique.** The ledger was rebuilt and its ids were reused, so two files may carry the
same id and be about entirely different work. `T-4-needs-you-page.md` is the "Needs you" dashboard page;
`T-4.md` is Discord ingest. Both are real, both are T-4, and neither is a mistake.

That is why the slug exists. Three specs — `T-4.md`, `T-11.md` and `T-12.md` — were each overwritten in
place by a spec for different work before anyone noticed, and reached `origin` that way. The originals were
recovered from git history and live here under their slugs.

**To find a spec, grep the headings, not the filenames:**

```
grep -m1 '^# ' specs/*.md
```

## The six files still named `T-<id>.md`

`T-1.md`, `T-1-readme.md`, `T-2.md`, `T-4.md`, `T-11.md`, `T-12.md` are the current generation. The live
ledger's `specPath` points at them, so `muthur task show T-11` would dangle if they moved. They were left
alone deliberately. New specs get a slug.

## The heading is what matters

`muthur task spec` reads a spec's first heading and refuses the file if it names a different task:

```
$ muthur task spec T-8 specs/T-9-outbound-gate.md
{"code":"spec_id_mismatch","message":"'specs/T-9-outbound-gate.md' is the spec for T-9, not T-8. ..."}
```

It also refuses a path to no file, and a path that escapes the repository. It does **not** check the
filename — a filename is a convention this repository keeps, while the heading is what an implementer
actually reads to find out what they are building.

So a spec's first line is `# T-<id> — <title>`, and it is worth getting right.
