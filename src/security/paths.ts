// Path and text boundaries (SD-023).
//
// The manifest can name files outside the analysis root: linked files compiled into a
// project are legitimate and must open. What must not happen is reading a file the
// manifest did not really describe (for example a crafted `..\..\secret` entry) or
// letting a value break out of the format it is rendered into.

/** True when relativePath stays inside rootDirectory (no `..` escape, no absolute path). */
export function isInsideRoot(rootDirectory: string, relativePath: string): boolean {
  void rootDirectory;
  const normalized = normalize(relativePath);
  if (normalized.startsWith('/') || /^[a-z]:\//i.test(normalized)) {
    return false;
  }

  const segments: string[] = [];
  for (const segment of normalized.split('/')) {
    if (segment === '' || segment === '.') {
      continue;
    }

    if (segment === '..') {
      if (segments.length === 0) {
        return false;
      }

      segments.pop();
      continue;
    }

    if (/[:*?"<>|]/.test(segment)) {
      // A name that cannot exist as a plain path segment is not trusted either.
      return false;
    }

    segments.push(segment);
  }

  return true;
}

/** Collapses a value to one line so it cannot break a label or a list item. */
export function sanitizeSingleLine(value: string, maxLength = 200): string {
  // Control characters are exactly what must go: they can fake line breaks in exports.
  // eslint-disable-next-line no-control-regex
  const single = value.replace(/[\r\n\t\u0000-\u001f\u007f]+/g, ' ').replace(/\s{2,}/g, ' ');
  return single.length > maxLength ? `${single.slice(0, maxLength)}…` : single;
}

function normalize(value: string): string {
  return value.replace(/\\/g, '/').replace(/\/+$/, '');
}

/** Root check used by callers that only need to know whether a path is inside. */
export function isOutsideRoot(rootDirectory: string, relativePath: string): boolean {
  return !isInsideRoot(rootDirectory, relativePath);
}
