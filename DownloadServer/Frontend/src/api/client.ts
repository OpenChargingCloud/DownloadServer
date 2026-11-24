import { apiURL,
         nodeAPI,
         request,
         type Certificate        as NodeCertificate,
         type CertificateImport  as NodeCertificateImport,
         type CertificateStore   as NodeCertificateStore,
         type NodeConfiguration,
         type NodeMe,
         type NodeResource,
         type NodeStatus,
         type Operation }  from '@node/api/client';


// What every node answers - the log, name resolution, the time, the store,
// who is signed in - and how it is asked are WWCP_Node's, and every page here
// reads them from this module. What follows is what a download server says
// of its own: its resources, its configuration's own section, the kinds its
// store keeps - and the files, the downloads and the downloaders, below
// "/downloadServer".
export * from '@node/api/client';


/**
 * What a role may be allowed to touch on this download server: what every
 * node has, and the files - read, listed and saved; edited, published and
 * taken away - the record of the downloads - read -, the downloaders - read;
 * edited, made, given a new password, taken away - and downloading itself,
 * which charging stations run.
 */
export type Resource = NodeResource | 'files' | 'downloads' | 'downloaders' | 'download';

/** What somebody signed in to this download server may do: an operation on a resource, written "files:edit". */
export type Permission = `${Resource}:${Operation}`;

/** Who is signed in to the web interface. */
export type Me = NodeMe<Resource>;

/** How the node is doing right now: what every node says. */
export type Status = NodeStatus;

/**
 * What the download server is made of: every node's sections, and its own.
 * Only the shape the Configuration page relies on is named; the fields of
 * each section are drawn from whatever the server sends.
 */
export interface Configuration extends NodeConfiguration {
    downloadServer:  Record<string, unknown>;
}

/** What a certificate in the node's own store is for: the four kinds of TLS. */
export type CertificateKind = 'tlsRoot' | 'clientRoot' | 'tlsServer' | 'tlsIdentity';

/** One certificate in the node's own store. */
export type Certificate = NodeCertificate<CertificateKind>;

/** What an import into the node's own store sends. */
export type CertificateImport = NodeCertificateImport<CertificateKind>;

/** The node's own store, grouped the way it is shown. */
export type CertificateStore = NodeCertificateStore<CertificateKind>;


/** One file offered. */
export interface OfferedFile {
    area:           string;
    name:           string;
    /** What it is fetched by: "/firmware/station-4.2.1.bin". */
    path:           string;
    /** In bytes. */
    size:           number;
    /** When it was last written - "2026-11-24T10:15:00.123Z". */
    modified:       string;
    /** In lower-case hex. */
    sha256:         string;
    /** How often it was downloaded, where the list says. */
    downloads?:     number;
    lastDownload?:  string | null;
}

/** What GET /downloadServer/files answers. */
export interface Files {
    /** Every area there is. */
    areas:  string[];
    /** The files of the area asked for, or of every one. */
    files:  OfferedFile[];
}

/** What publishing a file answers. */
export interface Published {
    said:  string;
    file:  OfferedFile;
}

/** How a file was fetched. */
export type DownloadProtocol = 'HTTP' | 'SFTP';

/** One download, and how it went. */
export interface Download {
    /** Its number: one above the one before, never reused. */
    id:             number;
    timestamp:      string;
    protocol:       DownloadProtocol;
    /** The account it was fetched under, or "anonymous". */
    downloader:     string;
    area:           string;
    fileName:       string;
    /** 200 for the file from its first byte on, 206 for the rest of it. */
    status:         number;
    /** Where what was sent begins. */
    firstByte:      number;
    /** How many bytes were sent. */
    bytes:          number;
    /** How large the file was. */
    size:           number;
    /** Whether what was sent reaches the end of the file. */
    toTheEnd:       boolean;
    remoteAddress:  string;
    userAgent?:     string;
}

/** Which downloads are asked for: GET /downloadServer/downloads. */
export interface DownloadFilter {
    downloader?:  string;
    area?:        string;
    file?:        string;
    /** ISO 8601: at or after. */
    since?:       string;
    /** ISO 8601: before. */
    until?:       string;
    order?:       'newest' | 'oldest';
    skip?:        number;
    take?:        number;
}

/** What GET /downloadServer/downloads answers. */
export interface Downloads {
    /** How many match the filter in all. */
    total:        number;
    /** Those of them asked for, in the order asked for. */
    downloads:    Download[];
    /** Every account that ever downloaded - for the filter to offer. */
    downloaders:  string[];
    /** Every file ever downloaded, by its path - for the filter to offer. */
    files:        string[];
}

/** How the download server stands: GET /downloadServer. */
export interface DownloadServerStatus {
    /** The directory the areas lie in. */
    directory:        string;
    areas:            { name: string; files: number; size: number }[];
    /** The port HTTP downloads are served on, or null where they are not. */
    http:             { listening: boolean; url: string; tls: boolean; anonymous: boolean; refused: number } | null;
    /** SFTP on the SSH server, or null where files are not offered over it. */
    sftp:             { serving: boolean; port: number | null; passwords: boolean } | null;
    /** The largest file that may be published, in bytes. */
    maxFileSize:      number;
    /** The largest file that may be published through the web interface, in bytes: what its HTTP server takes. */
    webMaxFileSize:   number;
    files:            number;
    /** In bytes. */
    totalSize:        number;
    downloads:        number;
    /** In bytes. */
    downloadedBytes:  number;
    downloaders:      number;
    latest:           Download | null;
}

/** An account in the downloader role. */
export interface Downloader {
    id:            string;
    name:          string;
    disabled:      boolean;
    downloads:     number;
    lastDownload:  string | null;
}

/** What a change of the downloaders answers: what it said, the password where one was made - once - and the downloaders as they are now. */
export interface DownloadersChanged {
    said:         string;
    downloader:   string | null;
    password:     string | null;
    downloaders:  Downloader[];
}


/** The query string of a filter: only what it says. */
function queryOf(Filter: object): string {

    const query = new URLSearchParams();

    for (const [key, value] of Object.entries(Filter))
        if (value !== undefined && value !== null && value !== '')
            query.set(key, String(value));

    const text = query.toString();

    return text.length > 0 ? `?${text}` : '';

}

/** The path of a file below the files of the API. */
function filePath(Area: string, Name: string): string {
    return `/downloadServer/files/${encodeURIComponent(Area)}/${encodeURIComponent(Name)}`;
}


/**
 * Publish a file: its bytes as the body of a PUT, as they are - which the
 * JSON request of every node does not send. Same origin, so the session
 * cookie travels with it.
 */
async function publish(Area: string, Name: string, Content: Blob): Promise<Published> {

    const response = await fetch(apiURL(filePath(Area, Name)), {
                               method:       'PUT',
                               headers:      { 'Accept': 'application/json', 'Content-Type': 'application/octet-stream' },
                               credentials:  'same-origin',
                               body:         Content
                           });

    const text = await response.text();

    let json: unknown = null;

    try { json = text.length > 0 ? JSON.parse(text) : null; } catch { json = null; }

    if (!response.ok) {

        const said = json !== null && typeof json === 'object' && 'error' in json && typeof (json as { error: unknown }).error === 'string'
                         ? (json as { error: string }).error
                         : response.status === 413
                               ? 'The file is larger than the web interface takes. Publish it over SFTP, or put it in its area\'s directory.'
                               : `The download server answered ${response.status}.`;

        throw new Error(said);

    }

    return json as Published;

}


/** How the download server is asked: the routes every node has, typed with the server's own of them, and the files, downloads and downloaders. */
export const api = {

    ...nodeAPI<{ me: Me; status: Status; configuration: Configuration; kind: CertificateKind; store: CertificateStore }>(),

    downloadServer: {

        status:            ()                                     => request<DownloadServerStatus>  ('GET',  '/downloadServer'),

        files:             (Area?: string)                        => request<Files>                 ('GET',  '/downloadServer/files' + queryOf({ area: Area })),
        publish,
        removeFile:        (Area: string, Name: string)           => request<{ said: string }>      ('POST', `${filePath(Area, Name)}/remove`, {}),

        /** Where a file is, to save it. */
        contentURL:        (Area: string, Name: string)           => apiURL(`${filePath(Area, Name)}/content`),

        downloads:         (Filter: DownloadFilter)               => request<Downloads>             ('GET',  '/downloadServer/downloads' + queryOf(Filter)),

        downloaders:       ()                                     => request<Downloader[]>          ('GET',  '/downloadServer/downloaders'),
        addDownloader:     (Id: string, Name?: string)            => request<DownloadersChanged>    ('POST', '/downloadServer/downloaders', Name !== undefined && Name.length > 0 ? { id: Id, name: Name } : { id: Id }),
        newPassword:       (Id: string)                           => request<DownloadersChanged>    ('POST', '/downloadServer/downloaders/password', { id: Id }),
        removeDownloader:  (Id: string)                           => request<DownloadersChanged>    ('POST', '/downloadServer/downloaders/remove', { id: Id })

    }

};
