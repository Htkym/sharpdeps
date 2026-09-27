import { execFileSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { validateSnapshot } from '../../src/analyzer/reportV2Validation';
import type { AnalysisSnapshot, EvidenceRecord } from '../../src/analyzer/reportV2';
import { buildProjection } from '../../src/analyzer/graphProjection';

const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharpdeps-review-'));
const quick = path.resolve('analyzer/bin/quick/code-map.dll');
const semantic = path.resolve('analyzer/bin/semantic/sharpdeps-semantic-host.dll');
const dotnet = process.env.SHARPDEPTS_DOTNET ?? 'dotnet';
let sequence = 0;
const write = (name: string, text: string) => {
  const file = path.join(root, name);
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, text);
  return file;
};
const project = (tfm = '<TargetFramework>net10.0</TargetFramework>', items = '') =>
  `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>${tfm}</PropertyGroup>${items}</Project>`;
function run(target: string, mode: 'quick' | 'semantic', args: string[] = []) {
  const output = path.join(root, `result-${++sequence}`, 'report.json');
  execFileSync(
    dotnet,
    [mode === 'quick' ? quick : semantic, '--solution', target, '--output', output, ...args],
    { encoding: 'utf8', timeout: 60000 }
  );
  const snapshot = JSON.parse(
    fs.readFileSync(path.join(path.dirname(output), 'report-v2.json'), 'utf8')
  ) as AnalysisSnapshot;
  const evidence = fs
    .readFileSync(path.join(path.dirname(output), 'evidence.ndjson'), 'utf8')
    .split('\n')
    .filter(Boolean)
    .map((line) => JSON.parse(line) as EvidenceRecord);
  expect(validateSnapshot(snapshot)).toMatchObject({ ok: true });
  return { snapshot, evidence };
}
beforeAll(() => {
  expect(fs.existsSync(quick), 'Build both hosts before the contract tests.').toBe(true);
  expect(fs.existsSync(semantic), 'Build both hosts before the contract tests.').toBe(true);
});
afterAll(() => fs.rmSync(root, { recursive: true, force: true }));

describe('review regressions through the published analyzers', () => {
  it('retains failed projects and reports partial compilation per project', () => {
    const good = write('mixed/Good/Good.csproj', project());
    const broken = write('mixed/Broken/Broken.csproj', project());
    write('mixed/Good/Good.cs', 'public class Good {}');
    write('mixed/Broken/Broken.cs', 'public class Broken { public MissingGeneratedType Value; }');
    write(
      'mixed/Unavailable/Unavailable.csproj',
      '<Project Sdk="SharpDeps.Missing.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>'
    );
    for (const target of [good, broken])
      execFileSync(dotnet, ['restore', target], { encoding: 'utf8', timeout: 60000 });
    const solution = write(
      'mixed/Mixed.slnx',
      '<Solution><Project Path="Good/Good.csproj"/><Project Path="Broken/Broken.csproj"/><Project Path="Unavailable/Unavailable.csproj"/></Solution>'
    );
    const { snapshot } = run(solution, 'semantic');
    expect(snapshot.completeness).toBe('partial');
    expect(snapshot.projects.find((item) => item.name === 'Unavailable')?.loadState).toBe('failed');
    const nodes = buildProjection(snapshot, { granularity: 'project' }).nodes;
    expect(nodes.find((item) => item.name === 'Good')?.analysisStatus).toBe('complete');
    expect(nodes.find((item) => item.name === 'Broken')?.analysisStatus).toBe('partial');
    expect(nodes.find((item) => item.name === 'Unavailable')?.analysisStatus).toBe('failed');
  }, 90000);
  it('keeps evaluated project references without code usage and preserves mapped positions', () => {
    const target = write(
      'release/A/A.csproj',
      project(undefined, '<ItemGroup><ProjectReference Include="../B/B.csproj"/></ItemGroup>')
    );
    write('release/B/B.csproj', project());
    write('release/B/B.cs', 'namespace B; public class Second {}');
    write(
      'release/A/LineOnly.cs',
      'namespace A; public class LineOnly {\n#line 70\npublic First Value;\n#line default\n}'
    );
    write(
      'release/A/A.cs',
      'namespace A; public class First {} public class Consumer {\n#line 120 "Pages/Source.razor"\n public First Value;\n public First Create() => new First();\n#line default\n}'
    );
    const solution = write(
      'release/All.slnx',
      '<Solution><Project Path="A/A.csproj"/><Project Path="B/B.csproj"/></Solution>'
    );
    execFileSync(dotnet, ['restore', solution], { encoding: 'utf8', timeout: 60000 });
    const { snapshot, evidence } = run(solution, 'semantic');
    const evaluated = snapshot.relations.filter(
      (relation) => relation.basis === 'projectEvaluated'
    );
    expect(evaluated).toHaveLength(1);
    expect(
      buildProjection(snapshot, {
        granularity: 'project',
        filters: { basis: ['projectEvaluated'] }
      }).edges
    ).toHaveLength(1);
    expect(evidence.find((record) => record.relationId === evaluated[0].id)).toMatchObject({
      kind: 'projectEvaluated',
      physicalSpan: null,
      confidence: 'resolved'
    });
    expect(
      evidence.filter(
        (record) => record.kind === 'signature' && record.mappedLocation?.line === 119
      )[0]
    ).toMatchObject({
      mappedLocation: { relativePath: 'A/Pages/Source.razor', line: 119 },
      physicalSpan: { startLine: 2 }
    });
    expect(evidence.find((record) => record.kind === 'constructs')?.mappedLocation?.line).toBe(120);
    expect(evidence.find((record) => record.mappedLocation?.line === 69)).toMatchObject({
      mappedLocation: { relativePath: 'A/LineOnly.cs' },
      physicalSpan: { startLine: 2 }
    });
    expect(snapshot.types.some((type) => type.id === evaluated[0].sourceEntityId)).toBe(false);
    expect(fs.existsSync(target)).toBe(true);
  }, 90000);
  it('keeps same-named Quick projects distinct and separates analysis from display limits', () => {
    const first = write(
      'quick/One/Common.csproj',
      project(
        undefined,
        '<ItemGroup><ProjectReference Include="../Two/Common.csproj"/></ItemGroup>'
      )
    );
    write('quick/Two/Common.csproj', project());
    write('quick/One/A.cs', 'using Two; namespace One; public class A {}');
    write('quick/Two/B.cs', 'namespace Two; public class B {}');
    const { snapshot } = run(first, 'quick', ['--max-projects', '1', '--max-edges', '1']);
    expect(snapshot.projects).toHaveLength(2);
    const namespaces = snapshot.namespaces;
    expect(new Set(namespaces.map((node) => node.projectVariantId)).size).toBe(2);
    const declared = snapshot.relations.find((relation) => relation.basis === 'projectDeclared')!;
    expect(declared.sourceEntityId).not.toBe(declared.targetEntityId);
    const graph = buildProjection(snapshot, { granularity: 'project' });
    expect(graph.edges).toHaveLength(2);
    expect(new Set(graph.edges.map((edge) => edge.basis))).toEqual(
      new Set(['projectDeclared', 'usingInferred'])
    );
  });

  it('retains initializer/type-argument evidence, file-local identity and complete cycle paths', () => {
    const target = write('semantic/Sample.csproj', project());
    write(
      'semantic/Input.cs',
      `
namespace Sample {
 public class Foo {} public class G<T> {}
 public class Service {
  object field = new Foo(); object Property { get; } = new Foo();
  public System.Collections.Generic.List<Foo> Qualified;
  void Run() { var g = new G<Foo>(); Foo local = null; if (local is Foo f) {} }
 }
 public class Outer { public class Inner {} }
 public class C { public D Other; } public class D { public C Other; }
}
namespace A { public class A1 { public B.B1 Field; } public class A2 {} }
namespace B { public class B1 {} public class B2 { public A.A2 Field; } }
`
    );
    write('semantic/LocalOne.cs', 'namespace Sample; file class Same {}');
    write('semantic/LocalTwo.cs', 'namespace Sample; file class Same {}');
    execFileSync(dotnet, ['restore', target], { encoding: 'utf8', timeout: 60000 });
    const { snapshot, evidence } = run(target, 'semantic');
    const foo = snapshot.types.find((type) => type.fullName === 'Sample.Foo')!;
    const references = evidence.filter((record) => record.targetTypeId === foo.id);
    expect(references.filter((record) => record.kind === 'constructs')).toHaveLength(2);
    expect(references.filter((record) => record.kind === 'typeUse')).toHaveLength(3);
    expect(references.filter((record) => record.kind === 'signature')).toHaveLength(1);
    expect(snapshot.types.filter((type) => type.name === 'Same')).toHaveLength(2);
    expect(snapshot.namespaces.map((node) => node.name).sort()).toEqual(['A', 'B', 'Sample']);
    expect(snapshot.cycleGroups.map((group) => group.scope).sort()).toEqual(['namespace', 'type']);
    for (const cycle of snapshot.cycleGroups) {
      expect(cycle.witness!.relationIds.length).toBeGreaterThan(1);
      expect(cycle.witness!.relationIds).toHaveLength(cycle.witness!.memberIds.length);
      expect(cycle.witness!.relationIds.every((id) => cycle.internalRelationIds.includes(id))).toBe(
        true
      );
    }
  }, 90000);

  it('keeps platform-specific TFMs distinct and applies an explicit profile', () => {
    const target = write(
      'platform/Platform.csproj',
      project('<TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks>')
    );
    const solution = write(
      'platform/Platform.slnx',
      '<Solution><Project Path="Platform.csproj"/></Solution>'
    );
    write('platform/Types.cs', 'public class A {}');
    execFileSync(dotnet, ['restore', target], { encoding: 'utf8', timeout: 60000 });
    const { snapshot } = run(solution, 'semantic');
    expect(
      snapshot.profile.projectVariants.map((variant) => variant.targetFramework).sort()
    ).toEqual(['net10.0', 'net10.0-windows']);
    expect(new Set(snapshot.types.map((type) => type.id)).size).toBe(2);
    const choice = snapshot.profile.projectVariants.find(
      (variant) => variant.targetFramework === 'net10.0-windows'
    )!;
    const selected = run(solution, 'semantic', [
      '--project-variants',
      JSON.stringify([
        { projectLogicalId: choice.projectLogicalId, targetFramework: choice.targetFramework }
      ])
    ]);
    expect(selected.snapshot.types).toHaveLength(1);
    expect(selected.snapshot.profile.projectVariants[0].targetFramework).toBe('net10.0-windows');
  }, 90000);

  it('reports a solution-load failure as failed, never complete', () => {
    const { snapshot } = run(write('broken/Broken.slnx', '<Solution><Broken>'), 'semantic');
    expect(snapshot.completeness).toBe('failed');
    expect(snapshot.limitations.some((item) => item.code === 'semantic.loadFailed')).toBe(true);
  });
});
