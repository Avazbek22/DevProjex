const dimensions = Object.freeze(['correctness', 'preference']);

export function reconcileOrderedAssessments(forward, reverse) {
  validateOrders(forward, reverse);
  return Object.fromEntries(dimensions.map(dimension => [
    dimension,
    reconcileDimension(forward, reverse, dimension),
  ]));
}

export function summarizeOrderedAssessments(pairs) {
  const reconciled = pairs.map(pair => reconcileOrderedAssessments(pair.forward, pair.reverse));
  const summary = { pairs: reconciled.length };
  for (const dimension of dimensions) {
    const disagreements = reconciled.filter(result => result[dimension].status === 'disagreement').length;
    summary[dimension] = {
      agreements: reconciled.length - disagreements,
      disagreements,
      disagreementRate: reconciled.length === 0 ? null : disagreements / reconciled.length,
    };
  }
  summary.anyDisagreement = {
    disagreements: reconciled.filter(result => dimensions.some(
      dimension => result[dimension].status === 'disagreement')).length,
  };
  summary.anyDisagreement.disagreementRate = reconciled.length === 0
    ? null
    : summary.anyDisagreement.disagreements / reconciled.length;
  return { summary, pairs: reconciled };
}

function reconcileDimension(forward, reverse, dimension) {
  const forwardChoice = canonicalChoice(forward, dimension);
  const reverseChoice = canonicalChoice(reverse, dimension);
  return forwardChoice === reverseChoice
    ? { status: 'verdict', choice: forwardChoice }
    : { status: 'disagreement', forward: forwardChoice, reverse: reverseChoice };
}

function canonicalChoice(assessment, dimension) {
  const choice = assessment[dimension];
  if (choice === 'tie')
    return 'tie';
  if (choice !== 'A' && choice !== 'B')
    throw new Error(`${dimension} must be A, B, or tie.`);
  return assessment.order[choice === 'A' ? 0 : 1];
}

function validateOrders(forward, reverse) {
  for (const assessment of [forward, reverse]) {
    if (!Array.isArray(assessment?.order) || assessment.order.length !== 2 ||
        assessment.order[0] === assessment.order[1])
      throw new Error('Each assessment requires two distinct ordered candidate ids.');
  }
  if (forward.order[0] !== reverse.order[1] || forward.order[1] !== reverse.order[0])
    throw new Error('The second assessment must reverse the first candidate order.');
}
