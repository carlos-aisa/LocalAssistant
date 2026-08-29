# Local document content read design

## Objective

Add an explicit, bounded local document read operation after metadata discovery.
The operation must not make the selected content available to the language model or
to a generic file tool.

## Scope

The API will expose `GET /api/documents/{id}/content`. It requires an authenticated
principal with the `documents.read` scope. `documents.search` alone is insufficient.

The metadata search response will return a protected, opaque document reference
instead of a random identifier. The reference contains the relative path without
revealing it to the client, is protected against modification, and expires after 15
minutes. Reading validates this reference before accessing the file.

The initial supported formats are `.txt`, `.md`, `.json`, and `.csv`. A file must be
no larger than 1 MiB before it is opened. The response returns the text and safe
metadata required to identify the result: name, extension, relative path, byte size,
and last modification time in UTC.

Before reading, the implementation resolves the selected path again from
`ILocalDocumentRoot`. It must confirm that the resolved destination remains inside
the configured root and that neither the file nor an ancestor is a reparse point.
The API never accepts a client-provided path as an alternative to the protected
reference.

## Data flow

```text
GET /api/documents
  -> protected reference, valid for 15 minutes

GET /api/documents/{id}/content
  -> authenticate API key
  -> require documents.read
  -> unprotect and validate reference
  -> resolve and authorize the file under the configured root
  -> validate extension and 1 MiB maximum size
  -> read bounded text
  -> return content and safe metadata
```

## Errors

- `401 Unauthorized` when no valid API key authenticates the request.
- `403 Forbidden` when the principal lacks `documents.read`.
- `404 Not Found` when the reference is invalid, modified, expired, or no longer
  resolves to an authorized file.
- `422 Unprocessable Content` when the selected file is not a supported format or
  exceeds 1 MiB.

## Out of scope

- Automatic model-tool registration, prompt construction, RAG, embeddings, indexing,
  background watches, OCR, and document-derived memory.
- PDF, Word, Excel, image, archive, or arbitrary binary readers.
- Partial responses, silent truncation, and arbitrary paths supplied by the client.
- Multiple document roots, per-document grants, persistence of search results, and
  document-content egress to external providers.

## Tests and documentation

Tests will cover search and read scopes independently, modified and expired
references, root containment, reparse points, supported extensions, the 1 MiB
boundary, successful UTF text reads, and the OpenAPI contract. README, architecture,
roadmap, and OpenAPI will document the separate read permission and fixed limits.

## Acceptance criteria

- An authorized principal with `documents.read` can read a supported, in-root file
  selected through a current protected reference.
- An anonymous principal or one with only `documents.search` cannot read content.
- The read operation cannot be redirected outside the configured root.
- Unsupported and oversized files fail explicitly without returning content.
- The language model receives no new file access capability.
- Formatting, Release build, and the complete deterministic test suite pass.
