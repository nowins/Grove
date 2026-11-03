#import <AppKit/AppKit.h>
#include <string.h>
#include <stdbool.h>

typedef struct { double x, y, width, height; int visible, hit; } GroveChromeRect;
static bool owned(NSWindow* window, NSView* view) {
    return [NSThread isMainThread] && window && view && view.window == window && [NSApp.windows containsObject:window];
}
static bool hits(NSWindow* window, NSView* view, NSPoint point, NSButton* button) {
    NSView* frame = window.contentView.superview;
    NSPoint converted = [frame convertPoint:[view convertPoint:point toView:nil] fromView:nil];
    NSView* hit = [frame hitTest:converted];
    return hit == button || (hit && [hit isDescendantOf:button]);
}
__attribute__((visibility("default"))) int grove_read_chrome(void* w, void* v, GroveChromeRect* rects, char* menu, int capacity, double* scale) {
    @autoreleasepool {
        NSWindow* window = (__bridge NSWindow*)w; NSView* view = (__bridge NSView*)v;
        if (!owned(window, view) || !rects || !menu || capacity < 1) return -1;
        for (int i=0; i<3; i++) {
            NSButton* button = [window standardWindowButton:(NSWindowButton)i];
            if (!button) { rects[i] = (GroveChromeRect){0}; continue; }
            NSRect frame = [view convertRect:button.bounds fromView:button];
            double y = view.isFlipped ? frame.origin.y : view.bounds.size.height - NSMaxY(frame);
            rects[i] = (GroveChromeRect){frame.origin.x, y, frame.size.width, frame.size.height,
                button && !button.isHiddenOrHasHiddenAncestor,
                hits(window, view, NSMakePoint(NSMidX(frame), NSMidY(frame)), button)};
        }
        NSString* title = NSApp.mainMenu.itemArray.firstObject.title ?: @"";
        strlcpy(menu, title.UTF8String, capacity); *scale = window.backingScaleFactor;
        return 0;
    }
}
__attribute__((visibility("default"))) int grove_hit_system_button(void* w, void* v, double x, double y) {
    @autoreleasepool {
        NSWindow* window = (__bridge NSWindow*)w; NSView* view = (__bridge NSView*)v;
        if (!owned(window, view)) return -1;
        NSPoint point = NSMakePoint(x, view.isFlipped ? y : view.bounds.size.height - y);
        for (int i=0; i<3; i++) {
            NSButton* button = [window standardWindowButton:(NSWindowButton)i];
            if (button && !button.isHiddenOrHasHiddenAncestor && hits(window, view, point, button)) return i+1;
        }
        return 0;
    }
}
__attribute__((visibility("default"))) int grove_click_system_button(void* w, int index) {
    @autoreleasepool {
        NSWindow* window = (__bridge NSWindow*)w;
        if (![NSThread isMainThread] || ![NSApp.windows containsObject:window] || index < 0 || index > 2) return -1;
        NSButton* button = [window standardWindowButton:(NSWindowButton)index];
        if (!button || !button.enabled || button.isHiddenOrHasHiddenAncestor) return -2;
        [button performClick:nil]; return 0;
    }
}
__attribute__((visibility("default"))) int grove_click_header(void* w, void* v, double x, double y) {
    @autoreleasepool {
        NSWindow* window = (__bridge NSWindow*)w; NSView* view = (__bridge NSView*)v;
        if (!owned(window, view)) return -1;
        NSPoint point = NSMakePoint(x, view.isFlipped ? y : view.bounds.size.height - y);
        NSPoint location = [view convertPoint:point toView:nil];
        for (int i=0; i<2; i++) {
            NSEvent* event = [NSEvent mouseEventWithType:i == 0 ? NSEventTypeLeftMouseDown : NSEventTypeLeftMouseUp
                location:location modifierFlags:0 timestamp:NSProcessInfo.processInfo.systemUptime windowNumber:window.windowNumber
                context:nil eventNumber:i clickCount:1 pressure:i == 0 ? 1 : 0];
            [window sendEvent:event];
        }
        return 0;
    }
}
