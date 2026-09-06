// Transitive lineage for the licence gate.
//
// WHY THIS EXISTS
//
// Three separate reviews of this register found the same defect three times, in
// three different sources, and each time a human reading a paper caught it
// rather than anything in the build:
//
//   a global relief model whose land layer is a non-commercial dataset;
//   an elevation mosaic vertically registered against a licensed source;
//   a surface model that acquired an obligation at a version bump.
//
// All three are one shape: a merged product importing another product's licence.
// Classifying by licence name misses it. Recording lineage in prose misses it
// too, because prose is not evaluated — the earlier registry described every one
// of these in a `notes` field while the gate waved the source through on its own
// class.
//
// So lineage becomes data. `derived_from` names the upstream registry keys a
// source is built from, and this module walks that graph transitively. A source
// is admissible only if it AND every ancestor is admissible. That makes the
// failure structural: a merged product cannot be added without naming what it is
// merged from, and naming an excluded input fails the build.
//
// The graph is also why source keys are version-pinned. A product can acquire an
// ancestor at a version bump, so `example@3.2` and `example@4.0` are separate
// nodes with different edges. Pinning is cheaper and more verifiable than any
// per-pixel mask, and this register has a live case of exactly that.

export interface LineageNode {
    readonly class: string;
    readonly derived_from?: readonly string[];
}

export interface LineageProblem {
    readonly code: string;
    /** Registry keys from the source under test to the offending ancestor. */
    readonly path: readonly string[];
    readonly message: string;
}

/** Renders a path as `child -> parent -> grandparent` for a message. */
function renderPath(path: readonly string[]): string {
    return path.join(" -> ");
}

/**
 * Walks a source's `derived_from` graph and reports every ancestor that would
 * not be admissible on its own.
 *
 * Returns problems rather than throwing, and returns ALL of them rather than the
 * first, so one run reports the whole picture. An unknown key and a cycle are
 * both problems, not silent stops: a lineage the gate cannot follow must not
 * read as a lineage it has cleared.
 *
 * @param key      registry key of the source being checked
 * @param sources  the whole registry, so ancestors can be looked up
 * @param allowed  classes admissible in the shipped path
 */
export function resolveLineage(
    key: string,
    sources: ReadonlyMap<string, LineageNode>,
    allowed: ReadonlySet<string>,
): LineageProblem[] {
    const problems: LineageProblem[] = [];
    // `visited` is global to the walk, not per-branch: a diamond — two paths to
    // the same ancestor — should report once, not twice.
    const visited = new Set<string>([key]);

    const walk = (path: readonly string[]): void => {
        const current = path[path.length - 1]!;
        const node = sources.get(current);
        if (!node) return;   // the caller reports an unknown head separately

        for (const parent of node.derived_from ?? []) {
            const next = [...path, parent];

            // A cycle means the registry describes a product derived from
            // itself. Report it rather than looping, and do not treat anything
            // beyond it as cleared.
            if (path.includes(parent)) {
                problems.push({
                    code: "derived-from-cycle",
                    path: next,
                    message: `lineage forms a cycle: ${renderPath(next)}`,
                });
                continue;
            }

            // The visited guard comes BEFORE reporting, not after. A diamond —
            // two paths reaching the same ancestor — is one fact about one
            // ancestor, and reporting it per-path turns a single upstream into
            // a list that scales with the shape of the graph rather than with
            // the number of problems.
            if (visited.has(parent)) continue;
            visited.add(parent);

            const parentNode = sources.get(parent);
            if (!parentNode) {
                problems.push({
                    code: "derived-from-unknown",
                    path: next,
                    message: `declares an upstream "${parent}" that is not in the registry `
                        + `(${renderPath(next)}). An unresolvable lineage cannot be cleared.`,
                });
                continue;
            }

            if (!allowed.has(parentNode.class)) {
                problems.push({
                    code: "derived-from-excluded",
                    path: next,
                    message: `is derived from "${parent}", class "${parentNode.class}", which may not enter `
                        + `the shipped path (${renderPath(next)}). A licence badge describes the aggregator's `
                        + `own rights, not the rights of everything inside it.`,
                });
                // Keep walking. An excluded ancestor may itself sit above
                // another, and naming the deepest cause is more useful than
                // stopping at the first one found.
            }

            walk(next);
        }
    };

    walk([key]);
    return problems;
}

/** Every ancestor of `key`, transitively. Used to report what a source pulls in. */
export function ancestorsOf(
    key: string,
    sources: ReadonlyMap<string, LineageNode>,
): string[] {
    const seen = new Set<string>();
    const stack = [...(sources.get(key)?.derived_from ?? [])];
    while (stack.length) {
        const next = stack.pop()!;
        if (seen.has(next)) continue;
        seen.add(next);
        stack.push(...(sources.get(next)?.derived_from ?? []));
    }
    return [...seen].sort();
}
