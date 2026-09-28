// Trust decisions (SD-023).
//
// Pure so the policy is testable without VS Code: an untrusted workspace must not run
// the analyzer (it evaluates MSBuild and project logic) or restore anything executable.
// Reading a stored result and showing it is data-only and stays allowed.

export interface TrustDecision {
  allowed: boolean;
  message?: string;
  /** Action the caller should offer when access is refused. */
  action?: 'manageTrust';
}

export function trustDecision(isTrusted: boolean): TrustDecision {
  if (isTrusted) {
    return { allowed: true };
  }

  return {
    allowed: false,
    message:
      'SharpDeps: このワークスペースは信頼されていません。解析はMSBuildの評価とプロジェクトのロジックを実行するため、信頼したうえで実行してください。',
    action: 'manageTrust'
  };
}
