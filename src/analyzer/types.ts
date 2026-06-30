// TypeScript shapes for the analyzer JSON output.
// The analyzer (analyzer/code-map.cs) serializes with a camelCase naming policy,
// so these interfaces use camelCase keys that match the emitted JSON directly.

export interface ProjectKindSummary {
  name: string;
  count: number;
}

export interface DependencyHubSummary {
  name: string;
  kind: string;
  outgoingDependencies: number;
  incomingDependencies: number;
  packageReferences: number;
}

export interface CodeMapProject {
  name: string;
  relativePath: string;
  groupPath: string;
  kind: string;
  targetFramework: string;
  outgoingDependencies: number;
  incomingDependencies: number;
  packageReferences: number;
}

export interface CodeMapDiagramProject {
  nodeId: string;
  lookupKey: string;
  name: string;
  kind: string;
  inCycle: boolean;
  /** Representative source file for the node. Populated for namespace nodes; null for project nodes. */
  representativeFile?: string | null;
}

export interface CodeMapDiagramEdge {
  edgeId: string;
  sourceKey: string;
  targetKey: string;
  sourceNodeId: string;
  targetNodeId: string;
  sourceName: string;
  targetName: string;
  count: number;
  inCycle: boolean;
}

export interface DependencyCycle {
  scope: string;
  nodes: string[];
  length: number;
}

export interface NamespaceGraph {
  namespaceCount: number;
  dependencyCount: number;
  mermaid: string;
  diagramNodes: CodeMapDiagramProject[];
  diagramEdges: CodeMapDiagramEdge[];
  cycles: DependencyCycle[];
  notes: string[];
}

export interface CodeMapReport {
  solutionPath: string;
  solutionName: string;
  projectCount: number;
  totalDependencies: number;
  totalPackageReferences: number;
  testProjectCount: number;
  projectKinds: ProjectKindSummary[];
  dependencyHubs: DependencyHubSummary[];
  notes: string[];
  warnings: string[];
  mermaid: string;
  projects: CodeMapProject[];
  diagramProjects: CodeMapDiagramProject[];
  diagramEdges: CodeMapDiagramEdge[];
  projectCycles: DependencyCycle[];
  namespaces: NamespaceGraph;
}
