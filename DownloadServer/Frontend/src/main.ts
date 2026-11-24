import './styles/app.scss';

// FontAwesome: the CSS ends up in the extracted stylesheet, the referenced
// font files become hashed assets below /assets/.
import '@fortawesome/fontawesome-free/css/fontawesome.css';
import '@fortawesome/fontawesome-free/css/solid.css';

import { html } from '@node/view';
import { nodeMenu, startNode } from '@node/start';

import { configurationPage } from './pages/configuration';
import { downloadersPage } from './pages/downloaders';
import { downloadServerPage } from './pages/downloadServer';
import { downloadsPage } from './pages/downloads';
import { filesPage } from './pages/files';

// What a download server has pages for of its own is the files - first in the
// menu, since they are what it is there for -, who downloaded what, how it
// offers them, at "/downloadServer", and the accounts they are downloaded
// under. Everything else is what every node has: its configuration, the name
// servers it asks itself, the time servers it checks its clock against, the
// SSH server - which SFTP is served on - the node's own certificate store and
// the log - see WWCP_Node's start.ts.
startNode({

    name:  'Download Server',
    icon:  'fa-download',

    menu: [
        { path: '/downloadServer/files', label: 'Files', icon: 'fa-file-arrow-down', permission: [ 'files:read' ] },
        { path: '/downloadServer/downloads', label: 'Downloads', icon: 'fa-list-check', permission: [ 'downloads:read' ] },
        { path: '/downloadServer', label: 'Download server', icon: 'fa-download', permission: [ 'files:read' ], children: [
            { path: '/downloadServer/downloaders', label: 'Downloaders', icon: 'fa-charging-station', permission: [ 'downloaders:read' ] }
        ] },
        nodeMenu.configuration([
            // The name servers it asks itself.
            nodeMenu.dns,
            nodeMenu.nts,
            nodeMenu.ssh,
            { ...nodeMenu.certificates, label: 'Trusted certificates' },
            nodeMenu.serverIdentities,
            nodeMenu.identities
        ]),
        nodeMenu.logs
    ],

    // The node's own store is what this server believes and shows: the roots
    // of the name and time servers it asks itself, and the identity its web
    // interface - and with it its download port - shows charging stations.
    certificates: {
        title:  'Trusted certificates',
        hints: {
            believes:  html`
                What this download server believes, beside the roots of this machine: a name server or a time server it
                asks itself, with a CA of its own - or a self-signed certificate - is reached through one of these.
            `,
            presents:  html`
                What it will show a server that asks who it is, with its private key. Kept for when this download server
                connects to one that does; nothing here presents it yet.
            `
        }
    },

    // "/" is every node's: the first page of the menu the person signed in may
    // open - the files for whoever may read them.
    pages: {

        '/configuration':              configurationPage,
        '/downloadServer':             downloadServerPage,
        '/downloadServer/files':       filesPage,
        '/downloadServer/downloads':   downloadsPage,
        '/downloadServer/downloaders': downloadersPage

    }

});
