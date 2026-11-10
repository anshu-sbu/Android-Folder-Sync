package com.local.foldersync;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.content.res.ColorStateList;
import android.database.Cursor;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.provider.DocumentsContract;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Switch;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONObject;

public class MainActivity extends Activity {
    private static final int PICK_FOLDER = 1, SCAN_QR = 3;
    private static final int BG = 0xFF110C08, CARD = 0xFF1D150E, FIELD = 0xFF2B2015, TEXT = 0xFFFAF2E8,
            MUTED = 0xFFA69480, ACCENT = 0xFFFF9933, ON_ACCENT = 0xFF1C1104, GOOD = 0xFF34D399, WARN = 0xFFFBBF24;

    private Prefs prefs;
    private TextView pcStatus, folderStatus, syncStatus;
    private Switch autoSwitch, mirrorSwitch, twoWaySwitch;
    private final Handler ui = new Handler();
    private final Runnable tick = new Runnable() {
        public void run() { refresh(); ui.postDelayed(this, 800); }
    };

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        prefs = new Prefs(this);
        buildUi();
        handleIntent(getIntent());
        if (Build.VERSION.SDK_INT >= 33) requestPermissions(new String[]{"android.permission.POST_NOTIFICATIONS"}, 2);
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        handleIntent(intent);
    }

    @Override
    protected void onResume() { super.onResume(); ui.post(tick); }

    @Override
    protected void onPause() { super.onPause(); ui.removeCallbacks(tick); }

    // ---------------------------------------------------------------- pairing

    private void handleIntent(Intent i) {
        if (i == null || i.getData() == null) return;
        Uri u = i.getData();
        if ("afs".equals(u.getScheme())) pairFrom(u.toString());
    }

    /** The PC's QR code: afs://host:tlsPort/token?f=certificateFingerprint */
    private void pairFrom(String link) {
        String host, token, fp;
        int port;
        try {
            Uri u = Uri.parse(link.trim());
            if (!"afs".equals(u.getScheme())) throw new Exception("not ours");
            host = u.getHost();
            port = u.getPort();
            token = u.getPathSegments().isEmpty() ? "" : u.getPathSegments().get(0);
            fp = u.getQueryParameter("f") == null ? "" : u.getQueryParameter("f");
            if (host == null || port <= 0 || token.isEmpty() || fp.isEmpty()) throw new Exception("bad code");
        } catch (Exception e) {
            toast("That isn't the pairing QR code from Folder Sync PC.");
            return;
        }
        String name = "";
        prefs.setPairing(host, port, token, name, fp);
        toast("Paired. Now choose a phone folder.");
        final Prefs p = prefs;
        new Thread(new Runnable() {
            public void run() {
                try {
                    JSONObject o = new JSONObject(Http.getString(p.url("/api/hello", null), p.fp()));
                    p.setPcName(o.optString("pc", ""));
                } catch (Exception e) {
                    final String m = e.getMessage();
                    runOnUiThread(new Runnable() { public void run() { toast("Couldn't reach the PC: " + m); } });
                }
            }
        }).start();
        maybeStart();
    }

    // ---------------------------------------------------------------- folder

    private void chooseFolder() {
        Intent i = new Intent(Intent.ACTION_OPEN_DOCUMENT_TREE);
        i.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_WRITE_URI_PERMISSION
                | Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION | Intent.FLAG_GRANT_PREFIX_URI_PERMISSION);
        startActivityForResult(i, PICK_FOLDER);
    }

    @Override
    protected void onActivityResult(int req, int res, Intent data) {
        super.onActivityResult(req, res, data);
        if (req == SCAN_QR && res == RESULT_OK && data != null) {
            String text = data.getStringExtra(ScanActivity.EXTRA_TEXT);
            if (text != null) pairFrom(text);
            return;
        }
        if (req == PICK_FOLDER && res == RESULT_OK && data != null && data.getData() != null) {
            Uri tree = data.getData();
            getContentResolver().takePersistableUriPermission(tree,
                    Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
            prefs.setTree(tree.toString());
            SyncService.kick = true;
            maybeStart();
        }
    }

    private String folderName() {
        if (prefs.tree() == null) return null;
        try {
            Uri tree = Uri.parse(prefs.tree());
            Uri doc = DocumentsContract.buildDocumentUriUsingTree(tree, DocumentsContract.getTreeDocumentId(tree));
            Cursor c = getContentResolver().query(doc, new String[]{DocumentsContract.Document.COLUMN_DISPLAY_NAME}, null, null, null);
            if (c != null) {
                try { if (c.moveToFirst()) return c.getString(0); } finally { c.close(); }
            }
        } catch (Exception ignored) { }
        return "(selected folder)";
    }

    private void maybeStart() {
        if (prefs.paired() && prefs.tree() != null && prefs.auto()) SyncService.start(this);
    }

    // ---------------------------------------------------------------- UI

    private int dp(int v) { return Math.round(v * getResources().getDisplayMetrics().density); }

    private GradientDrawable round(int color, int radiusDp) {
        GradientDrawable g = new GradientDrawable();
        g.setColor(color);
        g.setCornerRadius(dp(radiusDp));
        return g;
    }

    private TextView text(String s, int sp, int color, boolean bold) {
        TextView t = new TextView(this);
        t.setText(s);
        t.setTextSize(sp);
        t.setTextColor(color);
        if (bold) t.setTypeface(Typeface.DEFAULT_BOLD);
        return t;
    }

    private LinearLayout card(LinearLayout parent, String caption) {
        LinearLayout c = new LinearLayout(this);
        c.setOrientation(LinearLayout.VERTICAL);
        c.setBackground(round(CARD, 20));
        c.setPadding(dp(20), dp(18), dp(20), dp(20));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.bottomMargin = dp(16);
        parent.addView(c, lp);
        TextView cap = text(caption.toUpperCase(), 12, MUTED, true);
        cap.setLetterSpacing(0.08f);
        c.addView(cap);
        return c;
    }

    private Button button(String label, boolean primary, View.OnClickListener l) {
        Button b = new Button(this);
        b.setText(label);
        b.setAllCaps(false);
        b.setTextSize(17);
        b.setTypeface(Typeface.DEFAULT_BOLD);
        b.setTextColor(primary ? ON_ACCENT : ACCENT);
        // primary: solid saffron; secondary: saffron outline on a slightly lighter card, so it still reads as tappable
        GradientDrawable shape = round(primary ? ACCENT : 0xFF2A1D10, 16);
        if (!primary) shape.setStroke(dp(2), ACCENT);
        b.setBackground(new RippleDrawable(ColorStateList.valueOf(primary ? 0x44000000 : 0x44FF9933), shape, null));   // press feedback
        b.setMinHeight(dp(60));
        b.setOnClickListener(l);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.topMargin = dp(12);
        b.setLayoutParams(lp);
        return b;
    }

    private Switch toggle(String label, boolean on, android.widget.CompoundButton.OnCheckedChangeListener l) {
        Switch s = new Switch(this);
        s.setText(label);
        s.setTextSize(16);
        s.setTextColor(TEXT);
        s.setChecked(on);
        s.setMinHeight(dp(56));
        s.setThumbTintList(ColorStateList.valueOf(Color.WHITE));
        s.setTrackTintList(new ColorStateList(new int[][]{{android.R.attr.state_checked}, {}}, new int[]{ACCENT, 0xFF58432C}));
        s.setOnCheckedChangeListener(l);
        return s;
    }

    private void buildUi() {
        ScrollView sv = new ScrollView(this);
        sv.setBackgroundColor(BG);
        sv.setFillViewport(true);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(dp(18), dp(28), dp(18), dp(28));
        sv.addView(root);

        root.addView(text("Folder Sync Android", 28, TEXT, true));
        TextView sub = text("Keeps a phone folder in step with a folder on your PC, over Wi-Fi or the internet.", 15, MUTED, false);
        sub.setPadding(0, dp(4), 0, dp(20));
        root.addView(sub);

        LinearLayout pc = card(root, "1 · Your PC");
        pcStatus = text("", 17, TEXT, true);
        pcStatus.setPadding(0, dp(10), 0, dp(4));
        pc.addView(pcStatus);
        pc.addView(text("On the PC open \"Folder Sync PC\", then tap Scan below and point the camera at the pairing QR code it shows. Use the same Wi-Fi, this phone's hotspot, or pick Internet on the PC.", 14, MUTED, false));
        pc.addView(button("Scan QR code", true, new View.OnClickListener() {
            public void onClick(View v) { startActivityForResult(new Intent(MainActivity.this, ScanActivity.class), SCAN_QR); }
        }));
        pc.addView(text("Over the internet or public Wi-Fi it can be slow and may use mobile data you are charged for.", 13, MUTED, false));

        LinearLayout fo = card(root, "2 · Phone folder");
        folderStatus = text("", 17, TEXT, true);
        folderStatus.setPadding(0, dp(10), 0, dp(4));
        fo.addView(folderStatus);
        fo.addView(text("Android will ask you to allow Folder Sync Android to read and write the folder you pick. Files from the PC are copied into it and updated when they change.", 14, MUTED, false));
        fo.addView(button("Choose folder", true, new View.OnClickListener() {
            public void onClick(View v) { chooseFolder(); }
        }));

        LinearLayout sy = card(root, "3 · Sync");
        syncStatus = text("", 16, TEXT, false);
        syncStatus.setPadding(0, dp(10), 0, dp(8));
        sy.addView(syncStatus);
        autoSwitch = toggle("Keep syncing in the background", prefs.auto(), new android.widget.CompoundButton.OnCheckedChangeListener() {
            public void onCheckedChanged(android.widget.CompoundButton c, boolean on) {
                prefs.setAuto(on);
                if (on) maybeStart(); else SyncService.stop(MainActivity.this);
            }
        });
        sy.addView(autoSwitch);
        twoWaySwitch = toggle("Two-way sync (send changes made on this phone to the PC)", prefs.twoWay(), new android.widget.CompoundButton.OnCheckedChangeListener() {
            public void onCheckedChanged(android.widget.CompoundButton c, boolean on) {
                prefs.setTwoWay(on);
                SyncService.kick = true;
            }
        });
        sy.addView(twoWaySwitch);
        TextView twoNote = text("Also turn on Two-way sync in Folder Sync PC, which asks first. If both sides change the same file, both versions are kept.", 13, MUTED, false);
        twoNote.setPadding(0, 0, 0, dp(8));
        sy.addView(twoNote);
        mirrorSwitch = toggle("Sync deletions too (files deleted on one side are deleted on the other)", prefs.mirror(), new android.widget.CompoundButton.OnCheckedChangeListener() {
            public void onCheckedChanged(android.widget.CompoundButton c, boolean on) { prefs.setMirror(on); }
        });
        sy.addView(mirrorSwitch);
        sy.addView(button("Sync now", false, new View.OnClickListener() {
            public void onClick(View v) {
                if (!prefs.paired() || prefs.tree() == null) { toast("Pair with the PC and choose a folder first."); return; }
                SyncService.kick = true;
                SyncService.start(MainActivity.this);
                toast("Syncing…");
            }
        }));
        sy.addView(button("Forget this PC", false, new View.OnClickListener() {
            public void onClick(View v) {
                new android.app.AlertDialog.Builder(MainActivity.this)
                        .setTitle("Forget this PC?")
                        .setMessage("Syncing stops and the pairing is removed. Your files stay where they are. You can pair again by scanning the PC's QR code.")
                        .setNegativeButton("Cancel", null)
                        .setPositiveButton("Forget", new android.content.DialogInterface.OnClickListener() {
                            public void onClick(android.content.DialogInterface d, int which) {
                                SyncService.stop(MainActivity.this);
                                prefs.unpair();
                                refresh();
                                toast("Forgot this PC.");
                            }
                        })
                        .show();
            }
        }));

        setContentView(sv);
    }

    private void refresh() {
        if (prefs.paired()) {
            String pcn = prefs.pcName().isEmpty() ? "" : prefs.pcName() + " · ";
            pcStatus.setText("● Paired · " + pcn + prefs.host() + ":" + prefs.port());
            pcStatus.setTextColor(GOOD);
        } else {
            pcStatus.setText("○ Not paired yet");
            pcStatus.setTextColor(WARN);
        }
        String fn = folderName();
        if (fn == null) { folderStatus.setText("○ No folder chosen"); folderStatus.setTextColor(WARN); }
        else { folderStatus.setText("● " + fn); folderStatus.setTextColor(GOOD); }
        syncStatus.setText(SyncService.running ? SyncService.status : "Not running");
        syncStatus.setTextColor(SyncService.running ? TEXT : MUTED);
    }

    private void toast(String s) { Toast.makeText(this, s, Toast.LENGTH_LONG).show(); }
}
