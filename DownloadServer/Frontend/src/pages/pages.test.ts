/**
 * What the download server's own pages are held to: what every page of every
 * kind of node is, by the rules of WWCP_Node's test/pages.ts - the files page
 * has a form to publish one, the downloaders page one to make a downloader;
 * the downloads are filtered without one, and the download server and the
 * configuration have none.
 *
 * Run with `npm test`. "@node/.." is WWCP_Node/Frontend, where the rules are.
 */

import { everyPageIn } from '@node/../test/pages.ts';


everyPageIn(new URL('./', import.meta.url), {
    withForms: [ 'downloaders.ts', 'files.ts' ]
});
