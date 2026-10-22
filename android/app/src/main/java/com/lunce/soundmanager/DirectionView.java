package com.lunce.soundmanager;

import android.content.Context;
import android.graphics.*;
import android.view.MotionEvent;
import android.view.View;
import java.util.function.IntConsumer;

final class DirectionView extends View {
    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private int azimuth, originalAngle;
    private boolean dragging;
    private final IntConsumer changed;
    public DirectionView(Context context) { this(context, 0, value -> { }); }
    DirectionView(Context context, int angle, IntConsumer changed) {
        super(context); azimuth=angle; this.changed=changed;
        setContentDescription("방향 조작판. 표시된 점을 잡아 움직이세요. 아래 각도 슬라이더로도 조절할 수 있습니다.");
    }
    void setAzimuth(int value) { if (!dragging) { azimuth=value; invalidate(); } }
    private float radius() { return Math.min(getWidth(),getHeight())/2f-Ui.dp(getContext(),36); }
    private float handleX() { return getWidth()/2f+(float)Math.sin(Math.toRadians(azimuth))*radius(); }
    private float handleY() { return getHeight()/2f-(float)Math.cos(Math.toRadians(azimuth))*radius(); }
    @Override protected void onDraw(Canvas canvas) {
        float x=getWidth()/2f,y=getHeight()/2f,r=radius();
        paint.setColor(Color.rgb(54,76,104)); paint.setStyle(Paint.Style.STROKE); paint.setStrokeWidth(Ui.dp(getContext(),1));
        canvas.drawCircle(x,y,r,paint); canvas.drawLine(x-r,y,x+r,y,paint); canvas.drawLine(x,y-r,x,y+r,paint);
        paint.setStyle(Paint.Style.FILL); paint.setColor(Ui.SUB); paint.setTextSize(Ui.dp(getContext(),12)); paint.setTextAlign(Paint.Align.CENTER);
        canvas.drawText("앞",x,y-r-Ui.dp(getContext(),18),paint); canvas.drawText("뒤",x,y+r+Ui.dp(getContext(),24),paint);
        canvas.drawText("왼쪽",x-r-Ui.dp(getContext(),16),y+Ui.dp(getContext(),4),paint);
        canvas.drawText("오른쪽",x+r+Ui.dp(getContext(),16),y+Ui.dp(getContext(),4),paint);
        paint.setColor(Ui.TEXT); paint.setTextSize(Ui.dp(getContext(),30)); canvas.drawText(getContext().getString(R.string.angle_value,azimuth),x,y+Ui.dp(getContext(),10),paint);
        paint.setColor(Ui.ACCENT); canvas.drawCircle(handleX(),handleY(),Ui.dp(getContext(),12),paint);
    }
    @Override public boolean onTouchEvent(MotionEvent event) {
        switch (event.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                if (!isEnabled() || Math.hypot(event.getX()-handleX(),event.getY()-handleY())>Ui.dp(getContext(),26)) return false;
                originalAngle=azimuth; dragging=true; setPressed(true);
                getParent().requestDisallowInterceptTouchEvent(true); return true;
            case MotionEvent.ACTION_MOVE:
                if (!dragging) return false;
                azimuth=((int)Math.toDegrees(Math.atan2(event.getX()-getWidth()/2f,getHeight()/2f-event.getY()))+360)%360;
                invalidate(); return true;
            case MotionEvent.ACTION_UP:
                if (!dragging) return false;
                dragging=false; setPressed(false); getParent().requestDisallowInterceptTouchEvent(false);
                performClick(); changed.accept(azimuth); return true;
            case MotionEvent.ACTION_CANCEL:
                if (!dragging) return false;
                dragging=false; azimuth=originalAngle; setPressed(false);
                getParent().requestDisallowInterceptTouchEvent(false); invalidate(); return true;
            default: return dragging;
        }
    }
    @Override public boolean performClick() { super.performClick(); return true; }
}
