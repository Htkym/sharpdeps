// Webview request bridge (SD-013).
//
// Translates protocol v2 messages into ReportStore calls and back. The bridge owns
// the rules the webview must not be trusted with:
//   - unknown message types and malformed ids are rejected before touching the store,
//   - analysis-scoped messages must reference a registered analysis (stale ones fail),
//   - the webview never sends paths, byte offsets, or line numbers.
//
// Actions that need host-side work outside the store (starting or cancelling an
// analysis, opening an editor, exporting, persisting view state) are answered with an
// explicit "not implemented in this build" error until SD-014/SD-019/SD-021/SD-022
// implement them. They are never silently accepted.

import { ReportStore, ReportStoreError } from './reportStore';
import type { EvidencePage, SearchPage } from './reportStore';
import { PROTOCOL_VERSION, validateWebviewMessage } from '../view/protocolV2';
import type { EntitySummary, HostToWebviewMessage, Projection } from '../view/protocolV2';

export interface ReportBridgeOptions {
  pageSize?: number;
  maxProjectionNodes?: number;
  maxProjectionEdges?: number;
}

export interface ReportBridge {
  handle(message: unknown): Promise<HostToWebviewMessage>;
}

export function createReportBridge(
  store: ReportStore,
  options: ReportBridgeOptions = {}
): ReportBridge {
  const pageSize = options.pageSize ?? 100;

  async function handle(message: unknown): Promise<HostToWebviewMessage> {
    const validation = validateWebviewMessage(message);
    if (!validation.ok) {
      return {
        type: 'error',
        code:
          validation.code === 'unknownType' ? 'protocol.unknownType' : 'protocol.invalidMessage',
        message: validation.errors.join('; ')
      };
    }

    const request = validation.value;
    try {
      switch (request.type) {
        case 'ready':
          return {
            type: 'capabilities',
            protocolVersion: PROTOCOL_VERSION,
            analysisId: store.currentAnalysisId ?? null,
            capabilities: {
              typeGraph: true,
              evidence: true,
              generatedDocuments: false,
              cycleWitness: true,
              search: true
            }
          };

        case 'searchEntities': {
          const page: SearchPage = store.search(request.analysisId, request.query, {
            limit: request.limit ?? pageSize,
            cursor: request.cursor,
            granularity: request.granularity
          });
          return {
            type: 'searchResults',
            requestId: request.requestId,
            analysisId: request.analysisId,
            total: page.total,
            items: page.items,
            nextCursor: page.nextCursor ?? null
          };
        }

        case 'getEntityDetails': {
          const details = store.getEntityDetails(request.analysisId, request.entityId);
          if (!details) {
            return {
              type: 'error',
              requestId: request.requestId,
              code: 'store.unknownEntity',
              message: `Unknown entity: ${request.entityId}`
            };
          }

          return {
            type: 'details',
            requestId: request.requestId,
            analysisId: request.analysisId,
            entity: { ...details.entity } as unknown as Record<string, unknown>,
            dependencies: details.dependencies,
            dependents: details.dependents
          };
        }

        case 'getEvidencePage': {
          const page: EvidencePage = await store.getEvidencePage(
            request.analysisId,
            request.relationId,
            { limit: request.limit ?? pageSize, cursor: request.cursor }
          );
          return {
            type: 'evidencePage',
            requestId: request.requestId,
            analysisId: request.analysisId,
            relationId: page.relationId,
            total: page.total,
            items: page.items.map(
              (record) => ({ ...record }) as unknown as Record<string, unknown>
            ),
            nextCursor: page.nextCursor ?? null
          };
        }

        case 'getCycleWitness': {
          const report = store.getReport(request.analysisId);
          const group = report.cycleGroups.find((entry) => entry.id === request.cycleGroupId);
          if (!group) {
            return {
              type: 'error',
              requestId: request.requestId,
              code: 'store.unknownCycleGroup',
              message: `Unknown cycle group: ${request.cycleGroupId}`
            };
          }

          return {
            type: 'cycleWitness',
            requestId: request.requestId,
            analysisId: request.analysisId,
            cycleGroupId: request.cycleGroupId,
            witness: group.witness
              ? {
                  memberIds: group.witness.memberIds,
                  relationIds: group.witness.relationIds,
                  verified: true,
                  basis: group.basis
                }
              : null
          };
        }

        case 'getProjection': {
          const projection = store.getProjection(request.analysisId, {
            scope: request.scope,
            granularity: request.granularity,
            maxNodes: options.maxProjectionNodes ?? 300,
            maxEdges: options.maxProjectionEdges ?? 1000
          });

          const payload: Projection = {
            scope: request.scope,
            granularity: request.granularity,
            nodes: projection.nodes.map((node): EntitySummary => ({
              id: node.id,
              name: node.name,
              granularity: node.granularity,
              kind: node.kind,
              projectName: node.projectName,
              inCycle: node.inCycle,
              isExternal: node.isExternal
            })),
            edges: projection.edges.map((edge) => ({
              id: edge.relation.id,
              sourceId: edge.sourceId,
              targetId: edge.targetId,
              basis: edge.relation.basis,
              kinds: edge.relation.kinds,
              evidenceCount: edge.relation.evidenceCount,
              inCycle: projection.nodes.some((node) => node.id === edge.sourceId && node.inCycle),
              generatedEvidenceCount: edge.relation.generatedEvidenceCount,
              publicSurfaceEvidenceCount: edge.relation.publicSurfaceEvidenceCount
            })),
            totalNodeCount: projection.totalNodeCount,
            totalEdgeCount: projection.totalEdgeCount,
            truncated: projection.truncated
          };

          return {
            type: 'projection',
            requestId: request.requestId,
            analysisId: request.analysisId,
            projection: payload
          };
        }

        default:
          // analyze / cancelAnalysis / copyContext / export / openEvidence /
          // openDeclaration / persistViewState: host work outside the store.
          return {
            type: 'error',
            requestId: 'requestId' in request ? request.requestId : null,
            code: 'bridge.notImplemented',
            message: `The host does not implement '${request.type}' yet.`
          };
      }
    } catch (error) {
      if (error instanceof ReportStoreError) {
        return {
          type: 'error',
          requestId: 'requestId' in request ? request.requestId : null,
          analysisId: 'analysisId' in request ? request.analysisId : null,
          code: `store.${error.code}`,
          message: error.message
        };
      }

      return {
        type: 'error',
        requestId: 'requestId' in request ? request.requestId : null,
        code: 'bridge.failed',
        message: error instanceof Error ? error.message : String(error)
      };
    }
  }

  return { handle };
}
