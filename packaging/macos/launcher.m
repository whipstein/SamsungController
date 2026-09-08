// Keep a real, signed bundle process alive as the responsible application for
// Local Network privacy. Never exec() the shared .NET apphost in its place.
#import <AppKit/AppKit.h>

@interface ControllerDelegate : NSObject <NSApplicationDelegate>
@property(nonatomic, strong) NSTask *serverLauncher;
@property(nonatomic, copy) NSArray<NSString *> *arguments;
@property(nonatomic, strong) NSURL *launcherURL;
@property(nonatomic) int exitCode;
@property(nonatomic) BOOL stopping;
@end

@implementation ControllerDelegate
- (NSTask *)launch:(NSArray<NSString *> *)arguments completion:(void (^)(NSTask *))completion {
    NSTask *task = [NSTask new];
    task.executableURL = self.launcherURL;
    task.currentDirectoryURL = self.launcherURL.URLByDeletingLastPathComponent;
    task.arguments = arguments;
    NSMutableDictionary *environment = [NSProcessInfo.processInfo.environment mutableCopy];
    environment[@"SAMSUNG_CONTROLLER_BUNDLED_HOST"] = @"1";
    task.environment = environment;
    task.terminationHandler = ^(NSTask *finished) {
        dispatch_async(dispatch_get_main_queue(), ^{ completion(finished); });
    };
    NSError *error = nil;
    if (![task launchAndReturnError:&error]) {
        NSLog(@"Cannot start the bundled SamsungController launcher: %@", error.localizedDescription);
        return nil;
    }
    return task;
}
- (void)finish:(int)code {
    self.exitCode = code;
    [NSApp stop:nil];
    // Wake run() so command-line/smoke launches also exit without another event.
    [NSApp postEvent:[NSEvent otherEventWithType:NSEventTypeApplicationDefined
        location:NSZeroPoint modifierFlags:0 timestamp:0 windowNumber:0 context:nil
        subtype:0 data1:0 data2:0] atStart:NO];
}
- (void)applicationDidFinishLaunching:(NSNotification *)notification {
    (void)notification;
    NSArray *arguments = [self.arguments arrayByAddingObject:@"--wait-for-exit"];
    self.serverLauncher = [self launch:arguments completion:^(NSTask *task) {
        [self finish:task.terminationStatus];
    }];
    if (!self.serverLauncher) [self finish:1];
}
- (BOOL)applicationShouldHandleReopen:(NSApplication *)sender hasVisibleWindows:(BOOL)visible {
    (void)sender; (void)visible;
    // The managed launcher verifies/reuses the existing server and opens its UI.
    [self launch:self.arguments completion:^(NSTask *task) { (void)task; }];
    return NO;
}
- (NSApplicationTerminateReply)applicationShouldTerminate:(NSApplication *)sender {
    (void)sender;
    if (!self.serverLauncher.running) return NSTerminateNow;
    if (!self.stopping) {
        self.stopping = YES;
        NSTask *stop = [self launch:[self.arguments arrayByAddingObjectsFromArray:@[@"--stop", @"--no-browser"]]
            completion:^(NSTask *task) {
                self.stopping = NO;
                if (task.terminationStatus != 0) {
                    NSAlert *alert = [NSAlert new];
                    alert.messageText = @"SamsungController is still running";
                    alert.informativeText = @"Stop any active display operation, then use Display → Quit app. The server was not forcibly stopped.";
                    [alert runModal];
                }
            }];
        if (!stop) self.stopping = NO;
    }
    // Normal server exit also ends the waiting launcher and this app process.
    return NSTerminateCancel;
}
@end

int main(int argc, const char **argv) {
    @autoreleasepool {
        NSMutableArray *arguments = [NSMutableArray new];
        for (int index = 1; index < argc; index++) {
            NSString *argument = [NSString stringWithUTF8String:argv[index]];
            if (![argument hasPrefix:@"-psn_"]) [arguments addObject:argument];
        }
        NSURL *launcher = [NSBundle.mainBundle.resourceURL URLByAppendingPathComponent:@"server/SamsungController.App"];
        // --stop is a CLI command, not a second GUI application lifecycle.
        if ([arguments containsObject:@"--stop"]) {
            NSTask *task = [NSTask new];
            task.executableURL = launcher;
            task.arguments = arguments;
            NSError *error = nil;
            if (![task launchAndReturnError:&error]) { NSLog(@"%@", error.localizedDescription); return 1; }
            [task waitUntilExit];
            return task.terminationStatus;
        }
        [NSApplication sharedApplication];
        [NSApp setActivationPolicy:NSApplicationActivationPolicyAccessory];
        ControllerDelegate *delegate = [ControllerDelegate new];
        delegate.arguments = arguments;
        delegate.launcherURL = launcher;
        NSApp.delegate = delegate;
        [NSApp run];
        return delegate.exitCode;
    }
}
