import { Plugin } from '@opencode/plugin';
import { PLUGIN_ID, setupContextReuse } from './host-adapter.mjs';

export default Plugin.define({ id: PLUGIN_ID, setup: setupContextReuse });
