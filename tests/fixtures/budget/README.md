# Evaluation budget fixtures

`pointguard.entra.json` and `pointguard.parameters.json` are a copy of a real-world Entra template (about fifty variables built with `map`,
`filter`, `flatten` and `toObject` over data, and copy loops whose names and properties index into them) and its parameter file. They are the
regression input of `EvaluationBudgetPlanningTests`: the CLI's `what-if` path over this template used to exhaust the run's 5,000,000 value-node
budget (docs/format.md, "Limits") because every `variables('x')[copyIndex()]` read copied the whole variable. Keep them byte-identical to the
template that failed; do not tidy them.