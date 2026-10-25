package com.lunce.soundmanager;

import android.content.*;
import android.database.*;
import android.net.Uri;
import android.os.*;
import android.provider.OpenableColumns;
import java.io.*;

// A single read-only, grant-protected private APK; connection files are never exposed.
public final class UpdateFileProvider extends ContentProvider {
    @Override public boolean onCreate(){return true;}
    private void validate(Uri uri) throws FileNotFoundException {
        if(!uri.getAuthority().equals(getContext().getPackageName()+".updates")||!uri.getPath().equals("/update.apk"))throw new FileNotFoundException();
    }
    @Override public ParcelFileDescriptor openFile(Uri uri,String mode) throws FileNotFoundException {
        validate(uri);if(!mode.equals("r"))throw new FileNotFoundException();return ParcelFileDescriptor.open(AppUpdates.apk(getContext()),ParcelFileDescriptor.MODE_READ_ONLY);
    }
    @Override public String getType(Uri uri){return "application/vnd.android.package-archive";}
    @Override public Cursor query(Uri uri,String[] projection,String selection,String[] arguments,String sort) {
        try {validate(uri);}catch(FileNotFoundException error){return null;}
        String[] columns=projection==null?new String[]{OpenableColumns.DISPLAY_NAME,OpenableColumns.SIZE}:projection;
        MatrixCursor cursor=new MatrixCursor(columns);Object[] values=new Object[columns.length];
        for(int i=0;i<columns.length;i++)if(columns[i].equals(OpenableColumns.DISPLAY_NAME))values[i]="SoundManager-update.apk";else if(columns[i].equals(OpenableColumns.SIZE))values[i]=AppUpdates.apk(getContext()).length();
        cursor.addRow(values);return cursor;
    }
    @Override public Uri insert(Uri uri,ContentValues values){throw new UnsupportedOperationException();}
    @Override public int update(Uri uri,ContentValues values,String selection,String[] arguments){throw new UnsupportedOperationException();}
    @Override public int delete(Uri uri,String selection,String[] arguments){throw new UnsupportedOperationException();}
}
