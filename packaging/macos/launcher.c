// Native bundle entry point. Runtime/data files belong in Resources, not MacOS.
#include <mach-o/dyld.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

int main(int argc, char **argv) {
    (void)argc;
    char executable[PATH_MAX], resolved[PATH_MAX], target[PATH_MAX];
    uint32_t size = sizeof(executable);
    if (_NSGetExecutablePath(executable, &size) != 0 || realpath(executable, resolved) == NULL) {
        perror("Cannot locate SamsungController.app");
        return 1;
    }
    char *name = strrchr(resolved, '/');
    if (!name) return 1;
    *name = '\0';
    int length = snprintf(target, sizeof(target), "%s/../Resources/server/SamsungController.App", resolved);
    if (length < 0 || length >= sizeof(target)) return 1;
    argv[0] = target;
    execv(target, argv);
    perror("Cannot start SamsungController; copy the complete app bundle");
    return 1;
}
