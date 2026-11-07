const fs = require('node:fs');

function resolveReleaseTarget(target, environment = {}) {
  if (!['nuget-only', 'nuget-and-marketplace', 'all'].includes(target)) {
    throw new Error(
      'Select RELEASE_TARGET as nuget-only, nuget-and-marketplace, or all in reviewed main.'
    );
  }
  const selected = {
    include_vsix: target !== 'nuget-only',
    publish_marketplace: target !== 'nuget-only',
    publish_open_vsx: target === 'all'
  };
  if (selected.publish_marketplace && !environment.VSCE_PAT?.trim()) {
    throw new Error('The selected release target requires VSCE_PAT.');
  }
  if (selected.publish_open_vsx && !environment.OVSX_PAT?.trim()) {
    throw new Error('The selected release target requires OVSX_PAT.');
  }
  return selected;
}

function main() {
  const selected = resolveReleaseTarget(process.env.RELEASE_TARGET, process.env);
  if (!process.env.GITHUB_OUTPUT) throw new Error('GITHUB_OUTPUT is required.');
  const output = Object.entries(selected)
    .map(([name, value]) => `${name}=${value}`)
    .join('\n');
  fs.appendFileSync(process.env.GITHUB_OUTPUT, output + '\n');
}

module.exports = { resolveReleaseTarget };
if (require.main === module) {
  try {
    main();
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
