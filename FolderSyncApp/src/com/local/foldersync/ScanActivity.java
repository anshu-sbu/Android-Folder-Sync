package com.local.foldersync;

import android.Manifest;
import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Matrix;
import android.graphics.Paint;
import android.graphics.RectF;
import android.graphics.SurfaceTexture;
import android.hardware.Camera;
import android.os.Bundle;
import android.view.Gravity;
import android.view.Surface;
import android.view.TextureView;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.TextView;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;

import com.google.zxing.BarcodeFormat;
import com.google.zxing.BinaryBitmap;
import com.google.zxing.DecodeHintType;
import com.google.zxing.MultiFormatReader;
import com.google.zxing.PlanarYUVLuminanceSource;
import com.google.zxing.Result;
import com.google.zxing.common.HybridBinarizer;

import java.util.Collections;
import java.util.EnumMap;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Full-screen camera that reads the pairing QR code shown on the PC. Returns the text in EXTRA_TEXT. */
@SuppressWarnings("deprecation")
public class ScanActivity extends Activity implements TextureView.SurfaceTextureListener, Camera.PreviewCallback {
    static final String EXTRA_TEXT = "text";
    private static final int REQ_CAMERA = 5;

    private TextureView tv;
    private Camera cam;
    private int prevW, prevH;
    private final ExecutorService decoder = Executors.newSingleThreadExecutor();
    private volatile boolean busy, done;
    private MultiFormatReader reader;

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        Map<DecodeHintType, Object> hints = new EnumMap<>(DecodeHintType.class);
        hints.put(DecodeHintType.POSSIBLE_FORMATS, Collections.singletonList(BarcodeFormat.QR_CODE));
        hints.put(DecodeHintType.TRY_HARDER, Boolean.TRUE);
        reader = new MultiFormatReader();
        reader.setHints(hints);

        FrameLayout root = new FrameLayout(this);
        root.setBackgroundColor(Color.BLACK);
        tv = new TextureView(this);
        tv.setSurfaceTextureListener(this);
        root.addView(tv, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        root.addView(new Overlay(this), new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        TextView hint = new TextView(this);
        hint.setText("Point the camera at the QR code\nshown on your PC");
        hint.setTextColor(0xFFFAF2E8);
        hint.setTextSize(18);
        hint.setTypeface(Typeface.DEFAULT_BOLD);
        hint.setGravity(Gravity.CENTER);
        FrameLayout.LayoutParams hp = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.TOP);
        hp.topMargin = dp(70);
        root.addView(hint, hp);

        Button cancel = new Button(this);
        cancel.setText("Cancel");
        cancel.setAllCaps(false);
        cancel.setTextSize(17);
        cancel.setTypeface(Typeface.DEFAULT_BOLD);
        cancel.setTextColor(0xFFFAF2E8);
        GradientDrawable g = new GradientDrawable();
        g.setColor(0xCC42321F);
        g.setCornerRadius(dp(16));
        cancel.setBackground(g);
        cancel.setMinHeight(dp(60));
        cancel.setOnClickListener(new View.OnClickListener() { public void onClick(View v) { finish(); } });
        FrameLayout.LayoutParams cp = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM);
        cp.setMargins(dp(24), 0, dp(24), dp(40));
        root.addView(cancel, cp);
        setContentView(root);

        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.CAMERA}, REQ_CAMERA);
        }
    }

    private int dp(int v) { return Math.round(v * getResources().getDisplayMetrics().density); }

    @Override
    public void onRequestPermissionsResult(int code, String[] perms, int[] results) {
        if (code == REQ_CAMERA) {
            if (results.length > 0 && results[0] == PackageManager.PERMISSION_GRANTED) {
                if (tv.isAvailable()) startCamera(tv.getSurfaceTexture(), tv.getWidth(), tv.getHeight());
            } else {
                finish();   // no camera permission: the user can still paste the link in the app
            }
        }
    }

    // ---------------------------------------------------------------- camera
    private void startCamera(SurfaceTexture st, int vw, int vh) {
        if (cam != null || checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) return;
        try {
            cam = Camera.open();
            Camera.Parameters p = cam.getParameters();
            // a preview size close to 1280x720 keeps decoding fast but sharp
            Camera.Size best = null;
            for (Camera.Size s : p.getSupportedPreviewSizes()) {
                if (best == null || Math.abs(s.width * s.height - 1280 * 720) < Math.abs(best.width * best.height - 1280 * 720)) best = s;
            }
            if (best != null) p.setPreviewSize(best.width, best.height);
            if (p.getSupportedFocusModes().contains(Camera.Parameters.FOCUS_MODE_CONTINUOUS_PICTURE))
                p.setFocusMode(Camera.Parameters.FOCUS_MODE_CONTINUOUS_PICTURE);
            cam.setParameters(p);
            Camera.Size ps = cam.getParameters().getPreviewSize();
            prevW = ps.width;
            prevH = ps.height;

            Camera.CameraInfo info = new Camera.CameraInfo();
            Camera.getCameraInfo(0, info);
            int rot = getWindowManager().getDefaultDisplay().getRotation();
            int degrees = rot == Surface.ROTATION_90 ? 90 : rot == Surface.ROTATION_180 ? 180 : rot == Surface.ROTATION_270 ? 270 : 0;
            cam.setDisplayOrientation((info.orientation - degrees + 360) % 360);

            fit(vw, vh);
            cam.setPreviewTexture(st);
            cam.setPreviewCallback(this);
            cam.startPreview();
        } catch (Exception e) {
            stopCamera();
            finish();
        }
    }

    /** Centre-crop the (rotated) preview into the view without stretching it. */
    private void fit(int vw, int vh) {
        if (prevW == 0 || vw == 0 || vh == 0) return;
        float pw = prevH, ph = prevW;                       // portrait: sensor frame is rotated by 90 degrees
        float s = Math.max(vw / pw, vh / ph);
        Matrix m = new Matrix();
        m.setScale((pw * s) / vw, (ph * s) / vh, vw / 2f, vh / 2f);
        tv.setTransform(m);
    }

    private void stopCamera() {
        if (cam != null) {
            try { cam.setPreviewCallback(null); cam.stopPreview(); } catch (Exception ignored) { }
            cam.release();
            cam = null;
        }
    }

    @Override
    public void onPreviewFrame(final byte[] data, Camera c) {
        if (done || busy || data == null) return;
        busy = true;
        final byte[] copy = data.clone();
        final int w = prevW, h = prevH;
        decoder.execute(new Runnable() {
            public void run() {
                try {
                    PlanarYUVLuminanceSource src = new PlanarYUVLuminanceSource(copy, w, h, 0, 0, w, h, false);
                    Result r = reader.decodeWithState(new BinaryBitmap(new HybridBinarizer(src)));
                    final String text = r.getText();
                    if (text != null && !done) {
                        done = true;
                        runOnUiThread(new Runnable() {
                            public void run() {
                                Intent i = new Intent();
                                i.putExtra(EXTRA_TEXT, text);
                                setResult(RESULT_OK, i);
                                finish();
                            }
                        });
                    }
                } catch (Exception ignored) {
                    // no code in this frame
                } finally {
                    reader.reset();
                    busy = false;
                }
            }
        });
    }

    @Override public void onSurfaceTextureAvailable(SurfaceTexture st, int w, int h) { startCamera(st, w, h); }
    @Override public void onSurfaceTextureSizeChanged(SurfaceTexture st, int w, int h) { fit(w, h); }
    @Override public boolean onSurfaceTextureDestroyed(SurfaceTexture st) { stopCamera(); return true; }
    @Override public void onSurfaceTextureUpdated(SurfaceTexture st) { }

    @Override
    protected void onPause() { super.onPause(); stopCamera(); }

    @Override
    protected void onResume() {
        super.onResume();
        if (cam == null && tv != null && tv.isAvailable()) startCamera(tv.getSurfaceTexture(), tv.getWidth(), tv.getHeight());
    }

    @Override
    protected void onDestroy() { super.onDestroy(); decoder.shutdownNow(); }

    /** Dimmed screen with a clear square and saffron corner brackets. */
    private static class Overlay extends View {
        private final Paint dim = new Paint(), corner = new Paint();
        Overlay(Context c) {
            super(c);
            dim.setColor(0x99000000);
            corner.setColor(0xFFFF9933);
            corner.setStyle(Paint.Style.STROKE);
            corner.setStrokeWidth(10f);
            corner.setStrokeCap(Paint.Cap.ROUND);
        }

        @Override
        protected void onDraw(Canvas cv) {
            int w = getWidth(), h = getHeight();
            float side = Math.min(w, h) * 0.68f;
            float l = (w - side) / 2f, t = (h - side) / 2f - h * 0.04f, r = l + side, b = t + side;
            cv.drawRect(0, 0, w, t, dim);
            cv.drawRect(0, b, w, h, dim);
            cv.drawRect(0, t, l, b, dim);
            cv.drawRect(r, t, w, b, dim);
            float a = side * 0.14f;
            cv.drawLine(l, t, l + a, t, corner); cv.drawLine(l, t, l, t + a, corner);
            cv.drawLine(r, t, r - a, t, corner); cv.drawLine(r, t, r, t + a, corner);
            cv.drawLine(l, b, l + a, b, corner); cv.drawLine(l, b, l, b - a, corner);
            cv.drawLine(r, b, r - a, b, corner); cv.drawLine(r, b, r, b - a, corner);
        }
    }
}
