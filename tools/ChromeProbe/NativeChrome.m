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
        // App-menu holder items intentionally have an empty title in Avalonia.
        // Its native SetApplicationTitle sets the Cocoa process/display name.
        NSString* title = NSProcessInfo.processInfo.processName ?: @"";
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

static NSMenu* grove_submenu(const char* title) {
    [NSApp.mainMenu update];
    if (!title || !*title) return NSApp.mainMenu.itemArray.firstObject.submenu;
    NSString* name = [NSString stringWithUTF8String:title];
    for (NSMenuItem* item in NSApp.mainMenu.itemArray) if ([item.title isEqualToString:name]) return item.submenu;
    return nil;
}
__attribute__((visibility("default"))) int grove_menu_snapshot(char* output, int capacity) {
    @autoreleasepool {
        if (![NSThread isMainThread] || !output || capacity < 1) return -1;
        [NSApp.mainMenu update]; NSMutableArray* groups = [NSMutableArray array];
        for (NSMenuItem* group in NSApp.mainMenu.itemArray) {
            if (!group.submenu) continue; [group.submenu update]; NSMutableArray* entries = [NSMutableArray array];
            for (NSMenuItem* item in group.submenu.itemArray) {
                [entries addObject:@{ @"title": item.title ?: @"", @"enabled": @(item.enabled), @"separator": @(item.separatorItem),
                    @"key": item.keyEquivalent ?: @"", @"modifiers": @(item.keyEquivalentModifierMask) }];
            }
            [groups addObject:@{ @"title": group.title ?: @"", @"items": entries }];
        }
        NSData* data = [NSJSONSerialization dataWithJSONObject:groups options:0 error:nil];
        if (!data || data.length + 1 > capacity) return -2;
        memcpy(output, data.bytes, data.length); output[data.length] = 0; return 0;
    }
}
__attribute__((visibility("default"))) int grove_invoke_menu(const char* group, const char* title) {
    @autoreleasepool {
        if (![NSThread isMainThread] || !title) return -1;
        NSMenu* menu = grove_submenu(group); if (!menu) return -2; [menu update];
        NSString* name = [NSString stringWithUTF8String:title];
        NSInteger index = [menu indexOfItemWithTitle:name]; if (index < 0) return -3;
        NSMenuItem* item = [menu itemAtIndex:index]; if (!item.enabled || item.separatorItem) return -4;
        [menu performActionForItemAtIndex:index]; return 0;
    }
}
__attribute__((visibility("default"))) int grove_menu_key(void* w, const char* characters, unsigned short code) {
    @autoreleasepool {
        NSWindow* window = (__bridge NSWindow*)w;
        if (![NSThread isMainThread] || ![NSApp.windows containsObject:window] || !characters) return -1;
        if (!NSApp.isActive || !window.isKeyWindow) return -3;
        NSString* text = [NSString stringWithUTF8String:characters];
        for (int i=0; i<2; i++) {
            NSEvent* event = [NSEvent keyEventWithType:i == 0 ? NSEventTypeKeyDown : NSEventTypeKeyUp
                location:NSZeroPoint modifierFlags:NSEventModifierFlagCommand timestamp:NSProcessInfo.processInfo.systemUptime
                windowNumber:window.windowNumber context:nil characters:text charactersIgnoringModifiers:text isARepeat:NO keyCode:code];
            [NSApp postEvent:event atStart:NO];
        }
        // Actual NSApplication keyboard queue: menu routing and the focused
        // native view see the same key-down/up sequence. No system-wide input.
        return 0;
    }
}

__attribute__((visibility("default"))) int grove_activate_test_window(void* w) {
    @autoreleasepool {
        NSWindow* window = (__bridge NSWindow*)w;
        if (![NSThread isMainThread] || ![NSApp.windows containsObject:window]) return -1;
        [NSApp activateIgnoringOtherApps:YES]; [window makeKeyAndOrderFront:nil]; return 0;
    }
}
__attribute__((visibility("default"))) int grove_test_window_focus(void* w, char* output, int capacity) {
    @autoreleasepool {
        NSWindow* window = (__bridge NSWindow*)w;
        if (![NSThread isMainThread] || ![NSApp.windows containsObject:window] || !output || capacity < 1) return -1;
        NSDictionary* value = @{ @"appActive": @(NSApp.isActive), @"keyWindow": @(window.isKeyWindow),
            @"firstResponder": window.firstResponder ? NSStringFromClass(window.firstResponder.class) : @"" };
        NSData* data = [NSJSONSerialization dataWithJSONObject:value options:0 error:nil];
        if (!data || data.length + 1 > capacity) return -2;
        memcpy(output, data.bytes, data.length); output[data.length] = 0; return 0;
    }
}

__attribute__((visibility("default"))) int grove_focus_test_webview(void* w) {
    @autoreleasepool {
        NSWindow* window = (__bridge NSWindow*)w;
        if (![NSThread isMainThread] || ![NSApp.windows containsObject:window] || !window.isKeyWindow) return -1;
        Class webViewClass = NSClassFromString(@"WKWebView");
        if (!webViewClass || !window.contentView) return -2;
        NSMutableArray<NSView*>* remaining = [NSMutableArray arrayWithObject:window.contentView];
        while (remaining.count) {
            NSView* view = remaining.lastObject; [remaining removeLastObject];
            if (view.window == window && !view.isHiddenOrHasHiddenAncestor && [view isKindOfClass:webViewClass])
                return [window makeFirstResponder:view] ? 0 : -3;
            [remaining addObjectsFromArray:view.subviews];
        }
        return -4;
    }
}
