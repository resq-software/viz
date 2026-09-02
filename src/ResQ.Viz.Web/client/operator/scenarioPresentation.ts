// ResQ Viz - scenario display copy and environment lookup
// SPDX-License-Identifier: Apache-2.0

import { environmentFor } from '../scenarioEnvironments';

export interface ScenarioPresentation {
  readonly displayName: string;
  readonly category: string;
  readonly purpose: string;
  readonly environment: string | null;
}

type ScenarioCopy = Omit<ScenarioPresentation, 'environment'>;

const COPY: Readonly<Record<string, ScenarioCopy>> = {
  single: { displayName: 'Single', category: 'Exercise', purpose: 'Single-asset smoke test' },
  'swarm-5': { displayName: 'Swarm 5', category: 'Exercise', purpose: 'Five-aircraft formation' },
  'swarm-20': { displayName: 'Swarm 20', category: 'Exercise', purpose: 'Dense swarm coordination' },
  sar: { displayName: 'SAR', category: 'Response', purpose: 'Lead, scout, and relay search' },
  'multi-agency-sar': {
    displayName: 'Multi-agency SAR', category: 'Response', purpose: 'Three-agency air picture',
  },
  'wildfire-interface': {
    displayName: 'Wildfire Interface', category: 'Response', purpose: 'Wildland interface fire reconnaissance',
  },
  'hurricane-melissa': {
    displayName: 'Hurricane Melissa', category: 'Response', purpose: 'Coastal storm intelligence',
  },
  'flood-riverine': {
    displayName: 'Riverine Flood', category: 'Response', purpose: 'Flooded-valley survey',
  },
  'urban-collapse': {
    displayName: 'Urban Collapse', category: 'Response', purpose: 'Collapsed-structure search',
  },
  'alpine-sar': {
    displayName: 'Alpine SAR', category: 'Response', purpose: 'Avalanche response',
  },
  'canyon-sar': {
    displayName: 'Canyon SAR', category: 'Response', purpose: 'Gorge search',
  },
  'mixed-ground': {
    displayName: 'Mixed Ground', category: 'Mixed domain', purpose: 'Air and rover hillside operation',
  },
  'ground-convoy': {
    displayName: 'Ground Convoy', category: 'Mixed domain', purpose: 'Air-supported rover convoy',
  },
  'coastal-search': {
    displayName: 'Coastal Search', category: 'Mixed domain', purpose: 'Air, shore, and vessel search',
  },
  'coastal-transit': {
    displayName: 'Coastal Transit', category: 'Mixed domain', purpose: 'Air-supported vessel transit',
  },
  'flood-response': {
    displayName: 'Flood Response', category: 'Mixed domain', purpose: 'Mapping, supply, and ferry response',
  },
  'port-incident': {
    displayName: 'Port Incident', category: 'Mixed domain', purpose: 'Overwatch, cordon, and sampling',
  },
  'link-loss-divergence': {
    displayName: 'Link-loss Divergence', category: 'Safety', purpose: 'Cross-domain fallback comparison',
  },
  'mixed-load-150': {
    displayName: 'Mixed Load 150', category: 'Scale', purpose: '150-asset mixed-domain load',
  },
};

/** Humanizes a server-added slug without pretending it is curated copy. */
export function humaniseScenarioName(name: string): string {
  return name
    .trim()
    .split(/[-_\s]+/u)
    .filter(Boolean)
    .map(word => word.length <= 3 && word.toLowerCase() === 'sar'
      ? 'SAR'
      : `${word.charAt(0).toUpperCase()}${word.slice(1).toLowerCase()}`)
    .join(' ');
}

/** Resolves curated copy, with a safe visible fallback for future server presets. */
export function scenarioPresentation(name: string): ScenarioPresentation {
  const copy = Object.prototype.hasOwnProperty.call(COPY, name) ? COPY[name]! : {
    displayName: humaniseScenarioName(name),
    category: 'Other',
    purpose: 'Configured scenario',
  };
  const environment = environmentFor(name);
  return {
    ...copy,
    environment: environment === null
      ? null
      : humaniseScenarioName(environment.terrainPreset),
  };
}
