package com.lunce.soundmanager;

import android.content.Context;
import android.content.res.ColorStateList;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.view.Gravity;
import android.view.View;
import android.widget.*;

final class Ui {
    static final int BG = Color.rgb(13, 19, 30), PANEL = Color.rgb(24, 34, 50), TEXT = Color.rgb(237, 243, 255);
    static final int SUB = Color.rgb(157, 176, 201), ACCENT = Color.rgb(110, 231, 208), RED = Color.rgb(255, 151, 151);
    static int dp(Context c, float value) { return (int)(value*c.getResources().getDisplayMetrics().density+0.5f); }
    static GradientDrawable background(Context c, int color, int radius) {
        GradientDrawable value = new GradientDrawable(); value.setColor(color); value.setCornerRadius(dp(c, radius)); return value;
    }
    static LinearLayout column(Context c) { LinearLayout view = new LinearLayout(c); view.setOrientation(LinearLayout.VERTICAL); return view; }
    static LinearLayout row(Context c) { LinearLayout view = new LinearLayout(c); view.setGravity(Gravity.CENTER_VERTICAL); return view; }
    static LinearLayout card(LinearLayout parent) {
        Context c = parent.getContext(); LinearLayout view = column(c); int pad=dp(c,20); view.setPadding(pad,pad,pad,pad);
        view.setBackground(background(c, PANEL, 20));
        LinearLayout.LayoutParams layout = new LinearLayout.LayoutParams(-1,-2); layout.bottomMargin=dp(c,16); parent.addView(view,layout); return view;
    }
    static TextView text(LinearLayout parent, String title, int size, int color) {
        TextView view = new TextView(parent.getContext()); view.setText(title); view.setTextSize(size); view.setTextColor(color);
        view.setPadding(0,dp(parent.getContext(),4),0,dp(parent.getContext(),6)); parent.addView(view); return view;
    }
    static TextView title(LinearLayout parent, String title, int size) {
        TextView view=text(parent,title,size,TEXT); view.setTypeface(null,Typeface.BOLD); return view;
    }
    static Button button(LinearLayout parent, String label, Runnable action) {
        Context c=parent.getContext(); Button view=new Button(c); view.setText(label); view.setAllCaps(false); view.setTextColor(ACCENT);
        view.setMinHeight(dp(c,48)); view.setBackground(background(c,Color.rgb(35,53,70),12));
        LinearLayout.LayoutParams lp=new LinearLayout.LayoutParams(-1,dp(c,48)); lp.topMargin=dp(c,8); parent.addView(view,lp);
        view.setOnClickListener(v->action.run()); return view;
    }
    static void toggleStyle(Switch view) {
        int[][] states={new int[]{android.R.attr.state_checked},new int[]{}};
        view.setThumbTintList(new ColorStateList(states,new int[]{ACCENT,SUB}));
        view.setTrackTintList(new ColorStateList(states,new int[]{Color.rgb(53,107,102),Color.rgb(59,73,94)}));
        view.setTextColor(TEXT); view.setShowText(false); view.setMinHeight(dp(view.getContext(),48)); view.setSwitchPadding(dp(view.getContext(),16));
    }
    static void space(LinearLayout parent, int height) { parent.addView(new View(parent.getContext()),new LinearLayout.LayoutParams(1,dp(parent.getContext(),height))); }
}
