package com.local.foldersync;

import android.content.ContentResolver;
import android.content.Context;
import android.content.SharedPreferences;
import android.database.Cursor;
import android.net.Uri;
import android.provider.DocumentsContract;
import android.webkit.MimeTypeMap;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URLEncoder;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Locale;
import java.util.Map;
import java.util.Set;

/** One sync pass: make the chosen phone folder match the PC folder (PC -> phone). */
final class Syncer {
    interface Listener { void status(String s); }

    private static final class Doc {
        final String id;
        Doc(String id) { this.id = id; }
    }

    static String run(Context ctx, Listener l) throws Exception {
        Prefs p = new Prefs(ctx);
        if (!p.paired()) throw new Exception("Not paired with a PC yet.");
        if (p.tree() == null) throw new Exception("Choose a phone folder first.");
        Uri tree = Uri.parse(p.tree());
        ContentResolver cr = ctx.getContentResolver();
        String rootId = DocumentsContract.getTreeDocumentId(tree);

        l.status("Reading the PC folder…");
        JSONObject manifest = new JSONObject(Http.getString(p.url("/api/manifest", null), p.fp()));
        JSONArray files = manifest.getJSONArray("files");

        l.status("Reading the phone folder…");
        Map<String, Doc> have = new HashMap<>();
        Map<String, String> dirs = new HashMap<>();
        dirs.put("", rootId);
        list(cr, tree, rootId, "", have, dirs);

        SharedPreferences st = p.syncedState();
        SharedPreferences.Editor ed = st.edit();
        Set<String> remote = new HashSet<>();
        int total = files.length(), done = 0, skipped = 0, failed = 0;
        String lastError = null;

        for (int i = 0; i < total; i++) {
            JSONObject f = files.getJSONObject(i);
            String path = f.getString("p");
            long size = f.getLong("s");
            long mtime = f.getLong("m");
            remote.add(path);
            if (!safe(path)) { failed++; lastError = "Skipped unsafe path " + path; continue; }
            String stamp = size + ":" + mtime;
            Doc local = have.get(path);
            if (local != null && stamp.equals(st.getString(path, ""))) { skipped++; continue; }
            l.status("Downloading " + path + "  (" + (i + 1) + "/" + total + ")");
            try {
                download(cr, tree, p, path, local, dirs);
                ed.putString(path, stamp).apply();
                done++;
            } catch (Exception e) {
                failed++;
                lastError = path + ": " + e.getMessage();
            }
        }

        // files that left the PC folder
        int removed = 0;
        for (String key : new HashSet<>(st.getAll().keySet())) {
            if (remote.contains(key)) continue;
            Doc local = have.get(key);
            if (p.mirror() && local != null) {
                try {
                    DocumentsContract.deleteDocument(cr, DocumentsContract.buildDocumentUriUsingTree(tree, local.id));
                    removed++;
                } catch (Exception ignored) { }
            }
            ed.remove(key);
        }
        ed.apply();

        StringBuilder sb = new StringBuilder();
        sb.append(done).append(" new/updated, ").append(skipped).append(" already up to date");
        if (removed > 0) sb.append(", ").append(removed).append(" removed");
        if (failed > 0) sb.append(", ").append(failed).append(" failed (").append(lastError).append(")");
        return sb.toString();
    }

    private static boolean safe(String path) {
        if (path.isEmpty() || path.startsWith("/") || path.contains("\\")) return false;
        for (String seg : path.split("/")) if (seg.isEmpty() || seg.equals("..") || seg.equals(".")) return false;
        return true;
    }

    private static void list(ContentResolver cr, Uri tree, String docId, String rel,
                             Map<String, Doc> files, Map<String, String> dirs) {
        Uri kids = DocumentsContract.buildChildDocumentsUriUsingTree(tree, docId);
        Cursor cur = null;
        try {
            cur = cr.query(kids, new String[]{
                    DocumentsContract.Document.COLUMN_DOCUMENT_ID,
                    DocumentsContract.Document.COLUMN_DISPLAY_NAME,
                    DocumentsContract.Document.COLUMN_MIME_TYPE}, null, null, null);
            if (cur == null) return;
            while (cur.moveToNext()) {
                String id = cur.getString(0), name = cur.getString(1), mime = cur.getString(2);
                String path = rel.isEmpty() ? name : rel + "/" + name;
                if (DocumentsContract.Document.MIME_TYPE_DIR.equals(mime)) {
                    dirs.put(path, id);
                    list(cr, tree, id, path, files, dirs);
                } else {
                    files.put(path, new Doc(id));
                }
            }
        } finally {
            if (cur != null) cur.close();
        }
    }

    private static String ensureDir(ContentResolver cr, Uri tree, String rel, Map<String, String> dirs) throws Exception {
        String id = dirs.get(rel);
        if (id != null) return id;
        int cut = rel.lastIndexOf('/');
        String parentRel = cut < 0 ? "" : rel.substring(0, cut);
        String name = cut < 0 ? rel : rel.substring(cut + 1);
        String parentId = ensureDir(cr, tree, parentRel, dirs);
        Uri parent = DocumentsContract.buildDocumentUriUsingTree(tree, parentId);
        Uri made = DocumentsContract.createDocument(cr, parent, DocumentsContract.Document.MIME_TYPE_DIR, name);
        if (made == null) throw new Exception("Couldn't create folder " + rel);
        id = DocumentsContract.getDocumentId(made);
        dirs.put(rel, id);
        return id;
    }

    private static String mimeFor(String name) {
        int dot = name.lastIndexOf('.');
        if (dot > 0 && dot < name.length() - 1) {
            String m = MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substring(dot + 1).toLowerCase(Locale.ROOT));
            if (m != null) return m;
        }
        return "application/octet-stream";
    }

    private static void download(ContentResolver cr, Uri tree, Prefs p, String path, Doc local,
                                 Map<String, String> dirs) throws Exception {
        HttpURLConnection h = Http.open(p.url("/api/file", "p=" + URLEncoder.encode(path, "UTF-8")), p.fp());
        try {
            Uri target;
            if (local != null) {
                target = DocumentsContract.buildDocumentUriUsingTree(tree, local.id);
            } else {
                int cut = path.lastIndexOf('/');
                String parentRel = cut < 0 ? "" : path.substring(0, cut);
                String name = cut < 0 ? path : path.substring(cut + 1);
                String parentId = ensureDir(cr, tree, parentRel, dirs);
                Uri parent = DocumentsContract.buildDocumentUriUsingTree(tree, parentId);
                target = DocumentsContract.createDocument(cr, parent, mimeFor(name), name);
                if (target == null) throw new Exception("Couldn't create file");
            }
            InputStream in = h.getInputStream();
            OutputStream out = cr.openOutputStream(target, "wt");
            if (out == null) throw new Exception("Couldn't open file for writing");
            try {
                byte[] buf = new byte[64 * 1024];
                int n;
                while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
            } finally {
                out.close();
            }
        } finally {
            h.disconnect();
        }
    }
}
