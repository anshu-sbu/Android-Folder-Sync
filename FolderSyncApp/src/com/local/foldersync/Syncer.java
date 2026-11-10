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
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.HashMap;
import java.util.Locale;
import java.util.Map;
import java.util.TreeSet;

/**
 * One sync pass between the PC folder and the chosen phone folder.
 *
 * One-way mode: the PC folder is copied to the phone (as before).
 * Two-way mode: both sides are compared with what they looked like at the last sync ("base"), so a change is
 * attributed to the side it happened on - no clocks are compared between the two devices:
 *   changed on the PC only    -> download          changed on the phone only -> upload
 *   changed on both           -> keep both: the phone's version is kept as "name (phone copy ...)" on both sides
 *   deleted on one side       -> deleted on the other only if "sync deletions" is on; a deleted-vs-changed clash keeps the changed file
 */
final class Syncer {
    interface Listener { void status(String s); }

    /** Size + modified time of a file (PC: seconds, phone: milliseconds); null = the file doesn't exist. */
    private static final class St {
        final long size, time;
        St(long size, long time) { this.size = size; this.time = time; }
        String enc() { return size + ":" + time; }
        static St dec(String x) {
            if (x == null || x.equals("-") || x.equals("?")) return null;
            String[] a = x.split(":");
            return new St(Long.parseLong(a[0]), Long.parseLong(a[1]));
        }
        static boolean same(St a, St b) { return a == null ? b == null : (b != null && a.size == b.size && a.time == b.time); }
    }

    private static final class Doc {
        final String id;
        final St st;
        Doc(String id, St st) { this.id = id; this.st = st; }
    }

    /** What both sides looked like at the last sync. */
    private static final class Base {
        St pc, ph;
        boolean phKnown;
    }

    private static final String[] BLOCKED = {".exe", ".dll", ".bat", ".cmd", ".com", ".scr", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
            ".msi", ".msp", ".lnk", ".hta", ".cpl", ".reg", ".inf", ".pif", ".jar", ".appx", ".msix"};
    private static final String[] RESERVED = {"CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"};

    // state of one pass
    private final Context ctx;
    private final Prefs p;
    private final ContentResolver cr;
    private final Uri tree;
    private final SharedPreferences st;
    private final Map<String, String> dirs = new HashMap<>();
    private final Listener l;
    private int down, up, delPhone, delPc, conflicts, skipped, localSkipped, failed, waiting;
    private String lastError;
    private boolean pcAllowsWrites = true;

    private Syncer(Context ctx, Listener l) {
        this.ctx = ctx;
        this.l = l;
        p = new Prefs(ctx);
        cr = ctx.getContentResolver();
        tree = Uri.parse(p.tree());
        st = p.syncedState();
    }

    static String run(Context ctx, Listener l) throws Exception {
        Prefs p = new Prefs(ctx);
        if (!p.paired()) throw new Exception("Not paired with a PC yet.");
        if (p.tree() == null) throw new Exception("Choose a phone folder first.");
        return new Syncer(ctx, l).pass();
    }

    // ---------------------------------------------------------------- the pass
    private String pass() throws Exception {
        boolean two = p.twoWay(), mirror = p.mirror();
        String rootId = DocumentsContract.getTreeDocumentId(tree);

        l.status("Reading the PC folder…");
        JSONObject manifest = new JSONObject(Http.getString(p.url("/api/manifest", null), p.fp()));
        JSONArray files = manifest.getJSONArray("files");
        Map<String, St> pc = new HashMap<>();
        for (int i = 0; i < files.length(); i++) {
            JSONObject f = files.getJSONObject(i);
            pc.put(f.getString("p"), new St(f.getLong("s"), f.getLong("m")));
        }
        if (two) {
            try {
                JSONObject h = new JSONObject(Http.getString(p.url("/api/hello", null), p.fp()));
                pcAllowsWrites = h.optBoolean("twoWay", false);
            } catch (Exception e) {
                pcAllowsWrites = false;
            }
        }

        l.status("Reading the phone folder…");
        Map<String, Doc> have = new HashMap<>();
        dirs.put("", rootId);
        list(rootId, "", have);

        Map<String, String> baseRaw = new HashMap<>();
        for (Map.Entry<String, ?> e : st.getAll().entrySet()) if (e.getValue() instanceof String) baseRaw.put(e.getKey(), (String) e.getValue());

        TreeSet<String> paths = new TreeSet<>();
        paths.addAll(pc.keySet());
        paths.addAll(baseRaw.keySet());
        if (two) paths.addAll(have.keySet());

        int total = paths.size(), n = 0, same = 0;
        for (String path : paths) {
            n++;
            St pcNow = pc.get(path);
            Doc d = have.get(path);
            St phNow = d == null ? null : d.st;
            Base b = baseRaw.containsKey(path) ? loadBase(baseRaw.get(path)) : null;
            try {
                if (!safe(path)) { if (pcNow != null) { failed++; lastError = "Skipped unsafe path " + path; } continue; }
                if (!two) { if (!oneWay(path, pcNow, d, b, mirror, n, total)) same++; continue; }
                if (!twoWay(path, pcNow, d, phNow, b, mirror, n, total)) same++;
            } catch (Exception e) {
                failed++;
                lastError = path + ": " + e.getMessage();
                if (e instanceof Http.HttpError && ((Http.HttpError) e).code == 403) pcAllowsWrites = false;
            }
        }

        StringBuilder sb = new StringBuilder();
        sb.append(down).append(" downloaded");
        if (two) sb.append(", ").append(up).append(" sent");
        sb.append(", ").append(same).append(" up to date");
        if (delPhone > 0) sb.append(", ").append(delPhone).append(" removed here");
        if (delPc > 0) sb.append(", ").append(delPc).append(" removed on the PC");
        if (conflicts > 0) sb.append(", ").append(conflicts).append(" kept as copies");
        if (waiting > 0) sb.append(", ").append(waiting).append(" waiting: turn on Two-way sync in Folder Sync PC");
        if (failed > 0) sb.append(", ").append(failed).append(" failed (").append(lastError).append(")");
        return sb.toString();
    }

    /** One-way: the PC wins. Returns true if something was done. */
    private boolean oneWay(String path, St pcNow, Doc d, Base b, boolean mirror, int n, int total) throws Exception {
        if (pcNow != null) {
            boolean needs = d == null || b == null || !St.same(pcNow, b.pc);
            if (!needs) return false;
            l.status("Downloading " + path + "  (" + n + "/" + total + ")");
            downloadTo(path, pcNow, d);
            return true;
        }
        if (b != null) {
            if (mirror && d != null) { deletePhone(d); delPhone++; }
            remove(path);
            return mirror && d != null;
        }
        return false;
    }

    /** Two-way: decide per file using the base. Returns true if something was done. */
    private boolean twoWay(String path, St pcNow, Doc d, St phNow, Base b, boolean mirror, int n, int total) throws Exception {
        if (pcNow == null && phNow == null) { if (b != null) remove(path); return false; }
        St pcBase = b == null ? null : b.pc;
        boolean pcCh = !St.same(pcNow, pcBase);
        boolean phCh;
        if (b == null) phCh = phNow != null;
        else if (!b.phKnown) phCh = false;                  // synced before two-way existed: assume the phone copy is as we left it
        else phCh = !St.same(phNow, b.ph);
        if (b != null && !b.phKnown && phNow == null && pcNow != null) { pcCh = true; phCh = false; }   // missing on the phone: bring it back

        if (!pcCh && !phCh) return false;

        if (pcCh && !phCh) {
            if (pcNow != null) { l.status("Downloading " + path + "  (" + n + "/" + total + ")"); downloadTo(path, pcNow, d); return true; }
            // deleted on the PC, untouched on the phone
            if (mirror) { deletePhone(d); delPhone++; remove(path); return true; }
            put(path, null, phNow);                          // keep it here and don't send it back
            return false;
        }
        if (!pcCh) {
            if (phNow != null) return upload(path, d, pcNow, n, total);
            // deleted on the phone, untouched on the PC
            if (mirror) return deleteOnPc(path, pcNow);
            put(path, pcNow, null);
            return false;
        }
        // changed on both sides
        if (pcNow != null && phNow != null) {
            if (b == null && pcNow.size == phNow.size) { put(path, pcNow, phNow); return false; }   // same name, same size, never synced: treat as already in step
            return conflict(path, pcNow, d, n, total);
        }
        if (pcNow == null) return upload(path, d, null, n, total);                                      // deleted on the PC but changed here: keep the change
        l.status("Downloading " + path + "  (" + n + "/" + total + ")");                                  // deleted here but changed on the PC: bring it back
        downloadTo(path, pcNow, null);
        return true;
    }

    // ---------------------------------------------------------------- actions
    private boolean upload(String path, Doc d, St pcNow, int n, int total) throws Exception {
        if (!uploadable(path)) { localSkipped++; return false; }
        if (!pcAllowsWrites) { waiting++; return false; }
        l.status("Sending " + path + "  (" + n + "/" + total + ")");
        InputStream in = cr.openInputStream(DocumentsContract.buildDocumentUriUsingTree(tree, d.id));
        if (in == null) throw new Exception("Couldn't read the file");
        try {
            String url = p.url("/api/file", "p=" + enc(path) + "&m=" + (d.st.time / 1000)
                    + "&ps=" + (pcNow == null ? "-" : String.valueOf(pcNow.size)) + "&pm=" + (pcNow == null ? "-" : String.valueOf(pcNow.time)));
            JSONObject o = new JSONObject(Http.send("PUT", url, p.fp(), in, d.st.size));
            put(path, new St(o.getLong("s"), o.getLong("m")), d.st);
            up++;
            return true;
        } catch (Http.HttpError e) {
            if (e.code == 409) { skipped++; return false; }          // the PC file changed meanwhile: the next pass sorts it out
            if (e.code == 415) { put(path, null, d.st); localSkipped++; return false; }   // a type the PC refuses: don't try again until it changes
            throw e;
        } finally {
            in.close();
        }
    }

    private boolean deleteOnPc(String path, St pcNow) throws Exception {
        if (!pcAllowsWrites) { waiting++; return false; }
        try {
            Http.send("DELETE", p.url("/api/file", "p=" + enc(path) + "&ps=" + pcNow.size + "&pm=" + pcNow.time), p.fp(), null, 0);
        } catch (Http.HttpError e) {
            if (e.code == 409) { skipped++; return false; }
            throw e;
        }
        remove(path);
        delPc++;
        return true;
    }

    /** Both sides changed the file: keep both. The phone's version becomes "name (phone copy ...)" here and on the PC. */
    private boolean conflict(String path, St pcNow, Doc d, int n, int total) throws Exception {
        if (!pcAllowsWrites) { waiting++; return false; }      // can't keep both on the PC: leave everything as it is
        int cut = path.lastIndexOf('/');
        String dir = cut < 0 ? "" : path.substring(0, cut + 1);
        String name = path.substring(dir.length());
        int dot = name.lastIndexOf('.');
        String stem = dot > 0 ? name.substring(0, dot) : name, ext = dot > 0 ? name.substring(dot) : "";
        String copyName = stem + " (phone copy " + new SimpleDateFormat("yyyyMMdd-HHmm", Locale.US).format(new Date()) + ")" + ext;
        Uri renamed = DocumentsContract.renameDocument(cr, DocumentsContract.buildDocumentUriUsingTree(tree, d.id), copyName);
        if (renamed == null) throw new Exception("couldn't keep a copy of the phone's version");
        Doc copy = new Doc(DocumentsContract.getDocumentId(renamed), d.st);
        l.status("Both changed " + path + ": keeping both  (" + n + "/" + total + ")");
        downloadTo(path, pcNow, null);
        conflicts++;
        upload(dir + copyName, copy, null, n, total);
        return true;
    }

    private void deletePhone(Doc d) throws Exception {
        DocumentsContract.deleteDocument(cr, DocumentsContract.buildDocumentUriUsingTree(tree, d.id));
    }

    /** Download the PC file to the phone path (overwriting the phone file if there is one) and remember both states. */
    private void downloadTo(String path, St pcNow, Doc local) throws Exception {
        HttpURLConnection h = Http.open(p.url("/api/file", "p=" + enc(path)), p.fp());
        try {
            Uri target;
            String id;
            if (local != null) {
                id = local.id;
                target = DocumentsContract.buildDocumentUriUsingTree(tree, id);
            } else {
                int cut = path.lastIndexOf('/');
                String parentRel = cut < 0 ? "" : path.substring(0, cut);
                String name = cut < 0 ? path : path.substring(cut + 1);
                String parentId = ensureDir(parentRel);
                Uri parent = DocumentsContract.buildDocumentUriUsingTree(tree, parentId);
                target = DocumentsContract.createDocument(cr, parent, mimeFor(name), name);
                if (target == null) throw new Exception("Couldn't create file");
                id = DocumentsContract.getDocumentId(target);
            }
            InputStream in = h.getInputStream();
            OutputStream out = cr.openOutputStream(target, "wt");
            if (out == null) throw new Exception("Couldn't open file for writing");
            try {
                byte[] buf = new byte[64 * 1024];
                int r;
                while ((r = in.read(buf)) > 0) out.write(buf, 0, r);
            } finally {
                out.close();
            }
            put(path, pcNow, docState(id));
            down++;
        } finally {
            h.disconnect();
        }
    }

    // ---------------------------------------------------------------- base bookkeeping
    private static Base loadBase(String v) {
        Base b = new Base();
        int bar = v.indexOf('|');
        if (bar < 0) { b.pc = St.dec(v); b.phKnown = false; }
        else {
            b.pc = St.dec(v.substring(0, bar));
            String ph = v.substring(bar + 1);
            b.phKnown = !ph.equals("?");
            b.ph = St.dec(ph);
        }
        return b;
    }

    private void put(String path, St pc, St ph) {
        st.edit().putString(path, (pc == null ? "-" : pc.enc()) + "|" + (ph == null ? "-" : ph.enc())).apply();
    }

    private void remove(String path) { st.edit().remove(path).apply(); }

    // ---------------------------------------------------------------- phone folder
    private St docState(String id) {
        Cursor cur = null;
        try {
            cur = cr.query(DocumentsContract.buildDocumentUriUsingTree(tree, id), new String[]{
                    DocumentsContract.Document.COLUMN_SIZE, DocumentsContract.Document.COLUMN_LAST_MODIFIED}, null, null, null);
            if (cur != null && cur.moveToFirst()) return new St(cur.isNull(0) ? 0 : cur.getLong(0), cur.isNull(1) ? 0 : cur.getLong(1));
        } catch (Exception ignored) {
        } finally {
            if (cur != null) cur.close();
        }
        return null;
    }

    private void list(String docId, String rel, Map<String, Doc> files) {
        Uri kids = DocumentsContract.buildChildDocumentsUriUsingTree(tree, docId);
        Cursor cur = null;
        try {
            cur = cr.query(kids, new String[]{
                    DocumentsContract.Document.COLUMN_DOCUMENT_ID,
                    DocumentsContract.Document.COLUMN_DISPLAY_NAME,
                    DocumentsContract.Document.COLUMN_MIME_TYPE,
                    DocumentsContract.Document.COLUMN_SIZE,
                    DocumentsContract.Document.COLUMN_LAST_MODIFIED}, null, null, null);
            if (cur == null) return;
            while (cur.moveToNext()) {
                String id = cur.getString(0), name = cur.getString(1), mime = cur.getString(2);
                String path = rel.isEmpty() ? name : rel + "/" + name;
                if (DocumentsContract.Document.MIME_TYPE_DIR.equals(mime)) {
                    dirs.put(path, id);
                    list(id, path, files);
                } else {
                    files.put(path, new Doc(id, new St(cur.isNull(3) ? 0 : cur.getLong(3), cur.isNull(4) ? 0 : cur.getLong(4))));
                }
            }
        } finally {
            if (cur != null) cur.close();
        }
    }

    private String ensureDir(String rel) throws Exception {
        String id = dirs.get(rel);
        if (id != null) return id;
        int cut = rel.lastIndexOf('/');
        String parentRel = cut < 0 ? "" : rel.substring(0, cut);
        String name = cut < 0 ? rel : rel.substring(cut + 1);
        String parentId = ensureDir(parentRel);
        Uri parent = DocumentsContract.buildDocumentUriUsingTree(tree, parentId);
        Uri made = DocumentsContract.createDocument(cr, parent, DocumentsContract.Document.MIME_TYPE_DIR, name);
        if (made == null) throw new Exception("Couldn't create folder " + rel);
        id = DocumentsContract.getDocumentId(made);
        dirs.put(rel, id);
        return id;
    }

    // ---------------------------------------------------------------- helpers
    private static String enc(String s) throws Exception { return URLEncoder.encode(s, "UTF-8"); }

    private static boolean safe(String path) {
        if (path.isEmpty() || path.startsWith("/") || path.contains("\\")) return false;
        for (String seg : path.split("/")) if (seg.isEmpty() || seg.equals("..") || seg.equals(".")) return false;
        return true;
    }

    /** Files that should go to the PC: not hidden/temporary, not a type the PC refuses, and a name Windows accepts. */
    private static boolean uploadable(String path) {
        String lower = path.toLowerCase(Locale.ROOT);
        for (String ext : BLOCKED) if (lower.endsWith(ext)) return false;
        String[] segs = path.split("/");
        for (int i = 0; i < segs.length; i++) {
            String s = segs[i];
            if (s.startsWith(".") || s.startsWith("~$") || s.endsWith("~")) return false;
            if (s.endsWith(".") || s.endsWith(" ")) return false;
            for (int k = 0; k < s.length(); k++) {
                char c = s.charAt(k);
                if (c < 32 || "<>:\"|?*\\".indexOf(c) >= 0) return false;
            }
            String stem = s.split("\\.")[0].toUpperCase(Locale.ROOT);
            for (String r : RESERVED) if (r.equals(stem)) return false;
        }
        String leaf = segs[segs.length - 1].toLowerCase(Locale.ROOT);
        return !(leaf.endsWith(".tmp") || leaf.endsWith(".part") || leaf.endsWith(".crdownload") || leaf.endsWith(".pending") || leaf.equals("desktop.ini") || leaf.equals("thumbs.db"));
    }

    private static String mimeFor(String name) {
        int dot = name.lastIndexOf('.');
        if (dot > 0 && dot < name.length() - 1) {
            String m = MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substring(dot + 1).toLowerCase(Locale.ROOT));
            if (m != null) return m;
        }
        return "application/octet-stream";
    }
}
